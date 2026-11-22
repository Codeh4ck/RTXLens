[CmdletBinding()]
param(
    [string] $IncidentNotes,
    [string] $OutputDirectory = [Environment]::GetFolderPath('DesktopDirectory'),
    [uri] $ServiceAddress = 'http://localhost:59273/'
)

$end = [DateTimeOffset]::UtcNow
$start = $end.AddHours(-48)
$ErrorActionPreference = 'Stop'

if (!$OutputDirectory) { throw 'Windows did not return a Desktop folder. Specify -OutputDirectory.' }

if (!$ServiceAddress.IsLoopback -or $ServiceAddress.Scheme -notin 'http','https' -or $ServiceAddress.AbsolutePath -ne '/' -or $ServiceAddress.UserInfo -or $ServiceAddress.Query -or $ServiceAddress.Fragment)
{
    throw 'ServiceAddress must be a loopback HTTP or HTTPS root.'
}

Add-Type -AssemblyName System.IO.Compression
$utf8 = [Text.UTF8Encoding]::new($false)
$culture = [Globalization.CultureInfo]::InvariantCulture
$notes = [Collections.Generic.List[string]]::new()

function Write-ReportText([string] $Name, [string] $Text) 
{
    $writer = [IO.StreamWriter]::new($archive.CreateEntry($Name).Open(), $utf8)
    try 
    { 
        $writer.Write($Text) 
    } 
    finally
    { 
        $writer.Dispose()
    }
}

function Get-ServiceJson([string] $Route)
{
    Invoke-RestMethod -Uri ([uri]::new($ServiceAddress, $Route)) -Method Get -UseBasicParsing -TimeoutSec 5 -MaximumRedirection 0
}

function Collect-DesktopReports 
{
    foreach ($product in 'RTXLens','RTXLens.Debug') 
    {
        $dir = Join-Path $env:LOCALAPPDATA "$product\Diagnostics"
        $count = 0

        try 
        {
            if (Test-Path -LiteralPath $dir -PathType Container) 
            {
                foreach ($file in Get-ChildItem -LiteralPath $dir -File -Filter '*.txt') 
                {
                    if ($file.Name -notmatch '^(crash|action)-(\d{8}T\d{13}Z)-\d+-[a-f0-9]{32}\.txt$') 
                    { 
                        continue 
                    }

                    $timestamp = [DateTimeOffset]::MinValue

                    if (![DateTimeOffset]::TryParseExact($Matches[2], "yyyyMMdd'T'HHmmssfffffff'Z'", $culture, [Globalization.DateTimeStyles]::AssumeUniversal, [ref]$timestamp)) 
                    {
                        $notes.Add("Skipped desktop report with invalid timestamp: $($file.Name)")
                        continue
                    }

                    if ($timestamp -lt $start -or $timestamp -gt $end) 
                    { 
                        continue 
                    }

                    try
                    {
                        Write-ReportText "desktop/$product/$($file.Name)" ([IO.File]::ReadAllText($file.FullName))
                        $count++
                    } 
                    catch
                    { 
                        $notes.Add("Could not collect $($file.Name): $($_.Exception.Message)") 
                    }
                }
            }
        } 
        catch
        { 
            $notes.Add("Could not scan $dir. $($_.Exception.Message)") 
        }

        $notes.Add("$product desktop reports in window: $count")
    }
}

function Collect-WindowsEvents 
{
    $writer = [IO.StreamWriter]::new($archive.CreateEntry('history/windows-application-events.txt').Open(), $utf8)
    $count = 0

    try 
    {
        foreach ($provider in '.NET Runtime','Application Error','Windows Error Reporting','Application Hang') 
        {
            try 
            {
                Get-WinEvent -FilterHashtable @{LogName='Application'; ProviderName=$provider; StartTime=$start.LocalDateTime; EndTime=$end.LocalDateTime} -ErrorAction Stop | ForEach-Object {
                    $when = [DateTimeOffset]$_.TimeCreated
                    if ($when -ge $start -and $when -le $end) 
                    {
                        $message = $_.Message
                        $values = ($_.Properties | ForEach-Object { $_.Value }) -join [Environment]::NewLine

                        if ("$message $values" -match '(?i)\bRTXLens\.(App|Service)\.exe\b') 
                        {
                            $writer.WriteLine(('{0:o} | {1} | Event {2} | Record {3}' -f $when.ToUniversalTime(), $provider, $_.Id, $_.RecordId))

                            if ($message) 
                            { 
                                $writer.WriteLine($message) 
                            } 
                            else 
                            { 
                                $writer.WriteLine($values)
                            }

                            $writer.WriteLine()
                            $count++
                        }
                    }
                }
            }
            catch 
            {
                if ($_.FullyQualifiedErrorId -notlike 'NoMatchingEventsFound*') 
                { 
                    $notes.Add("Windows events ($provider): $($_.Exception.Message)")
                }
            }
        }
    } 
    finally 
    { 
        $writer.Dispose()
    }

    $notes.Add("Matching Windows Application crash/hang events: $count (history/windows-application-events.txt)")
}

function New-HistorySummary {
    @{
        Counts = [ordered]@{ Information = 0; Warning = 0; Error = 0; Other = 0 }
        Messages = [Collections.Generic.List[string]]::new()
    }
}

function Add-HistorySummary($Stats, [string]$Path) {
    $counts = $Stats.Counts
    $notes.Add("$Path - $($counts.Information) information, $($counts.Warning) warning, $($counts.Error) error, $($counts.Other) other.")
    if ($Stats.Messages.Count) {
        $notes.Add("Warning/error entries (up to 30 in collection order, UTC):")
        $notes.AddRange($Stats.Messages)
    }
}

function Write-HistoryEntry($Entry, [IO.TextWriter] $Writer, $Stats)
{
    $when = if ($Entry.timestamp -is [datetime]) { [DateTimeOffset]$Entry.timestamp } else { [DateTimeOffset]::Parse($Entry.timestamp, $culture, [Globalization.DateTimeStyles]::AssumeUniversal) }

    if ($when -ge $start -and $when -le $end) 
    {
        $Writer.WriteLine((ConvertTo-Json -InputObject $Entry -Depth 10 -Compress))
        $level = [string]$Entry.level
        if (!$Stats.Counts.Contains($level)) { $level = 'Other' }
        $Stats.Counts[$level]++
        if ($level -in 'Warning','Error' -and $Stats.Messages.Count -lt 30) {
            $Stats.Messages.Add(('  {0:u}  {1}  {2}  {3}' -f $when.UtcDateTime,$level,$Entry.source,$Entry.message))
        }
        return 1
    }

    return 0
}

function Collect-ServiceHistory 
{
    $count = 0
    $writer = [IO.StreamWriter]::new($archive.CreateEntry('history/service-api.jsonl').Open(), $utf8)
	
    try 
    {
        $stats = New-HistorySummary
        $before = [long]0
        $instance = $null
		
        do 
        {
            $page = Get-ServiceJson "logs?limit=250&before=$before"
			
            if (!$page.instanceId -or $null -eq $page.entries) { throw 'Unexpected service log format.' }
            if ($instance -and $instance -ne $page.instanceId) { throw 'Service restarted during collection.' }
			
            $instance = $page.instanceId
            $entries = @($page.entries)
			
            if (!$entries.Count) { break }
			
            $oldest = [long]$entries[-1].sequence
			
            if ($oldest -le 0 -or ($before -ne 0 -and $oldest -ge $before)) { throw 'Service log cursor did not advance.' }
			
            foreach ($entry in $entries) { $count += Write-HistoryEntry $entry $writer $stats }
            $before = $oldest
			
        } while ($before -gt $page.firstSequence)
		
        $notes.Add("Service events in window: $count (API)")
        Add-HistorySummary $stats 'history/service-api.jsonl'
		
        return
    } 
    catch 
    {
		$notes.Add("Service log API incomplete ($count entries captured): $($_.Exception.Message) Trying the local journal.")
	}
    finally 
    { 
		$writer.Dispose() 
	}

    $journal = Join-Path $env:ProgramData 'RTXLens\Logs\events.jsonl'
    $count = 0
    $stats = New-HistorySummary
    $invalid = 0
	
    try 
    {
        $reader = [IO.File]::OpenText($journal)
		
        try 
        {
            $writer = [IO.StreamWriter]::new($archive.CreateEntry('history/service-journal.jsonl').Open(), $utf8)
            try 
            {
                while ($null -ne ($line = $reader.ReadLine()))
                {
                    try { $entry = ConvertFrom-Json -InputObject $line; $count += Write-HistoryEntry $entry $writer $stats }
                    catch { $invalid++ }
                }
            } 
            finally
            { 
                $writer.Dispose() 
            }
        } 
        finally 
        { 
            $reader.Dispose()
        }
		
        $notes.Add("Service events in window: $count (disk journal); malformed lines skipped: $invalid")
        Add-HistorySummary $stats 'history/service-journal.jsonl'
		
    } 
    catch
    { 
        $notes.Add("Local service journal unavailable: $($_.Exception.Message)") 
    }
}

function Collect-CurrentState {
    $system = [ordered]@{ CapturedAtUtc=[DateTimeOffset]::UtcNow.ToString('o'); TimeZone=[TimeZoneInfo]::Local.Id }

    try 
    {
        $os = Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion'
        $system.WindowsBuild = "$($os.CurrentBuildNumber).$($os.UBR)"
        $system.WindowsDisplayVersion = $os.DisplayVersion
    } 
    catch 
    { 
        $notes.Add("Windows version: $($_.Exception.Message)")
    }
    
    try 
    {
        $system.ServiceStatus = (Get-Service -Name RTXLensService).Status.ToString()
    } 
    catch 
    { 
        $notes.Add("Windows service status: $($_.Exception.Message)") 
    }

    try
    {
        $install = Get-ItemPropertyValue 'HKLM:\SOFTWARE\RTXLens' -Name InstallDirectory
        $system.DesktopVersion = (Get-Item -LiteralPath (Join-Path $install 'Desktop\RTXLens.App.exe')).VersionInfo.ProductVersion
    } 
    catch 
    { 
        $notes.Add("Installed desktop version: $($_.Exception.Message)") 
    }

    Write-ReportText 'current/system.json' (ConvertTo-Json $system)

    $routes = [ordered]@{'service-version'='version'; 'service-health'='health'; 'plugins'='plugins'; 'msi-settings'='plugins/msi-psu/settings'; 'msi-device'='plugins/msi-psu/device'}
    
    foreach ($name in $routes.Keys) 
    {
        try 
        {
            $value = Get-ServiceJson $routes[$name]
            Write-ReportText "current/$name.json" (ConvertTo-Json -InputObject $value -Depth 25)
        } 
        catch 
        { 
            $notes.Add("$name unavailable: $($_.Exception.Message)") 
        }
    }
}

$directory = [IO.Directory]::CreateDirectory([IO.Path]::GetFullPath($OutputDirectory)).FullName
$name = 'RTXLens-CrashReport-' + $end.ToString('yyyyMMddTHHmmssZ') + '-' + [guid]::NewGuid().ToString('N').Substring(0,8)
$zip = Join-Path $directory "$name.zip"
$partial = "$zip.partial"
$stream = [IO.File]::Open($partial, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)

try 
{
    try
    {
        $archive = [IO.Compression.ZipArchive]::new($stream, [IO.Compression.ZipArchiveMode]::Create, $true)
        try 
        {
            Write-Host 'Collecting RTX Lens crash evidence from the previous 48 hours...'
            Collect-DesktopReports
            Collect-WindowsEvents
            Collect-ServiceHistory
            Collect-CurrentState

            if (!$IncidentNotes)
            { 
                $IncidentNotes = 'Not supplied. Please include the exact click, old/new setting, approximate crash time, and whether MSI readings were visible when sharing the ZIP.' 
            }

            Write-ReportText 'summary.txt' ((@('RTX Lens desktop crash report', "From (UTC): $($start.ToString('o'))",
                "Through (UTC): $($end.ToString('o'))", 'Desktop crash/action reports, when available, are in desktop/.', 'Windows crash/hang events and the RTX Lens service log are separate files in history/. Open the .jsonl service log in a text editor to read every event.',
                'current/ contains a snapshot at collection time.', '', "Incident notes: $IncidentNotes", '') + $notes.ToArray()) -join [Environment]::NewLine)

        } 
        finally 
        { 
            $archive.Dispose()
        }

    } 
    finally
    { 
        $stream.Dispose() 
    }
    
    [IO.File]::Move($partial,$zip)
} 
finally 
{ 
    if ([IO.File]::Exists($partial)) { [IO.File]::Delete($partial) } 
}

Write-Host "Share this ZIP with RTX Lens support: $zip"
