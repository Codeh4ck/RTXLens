#include <windows.h>
#include <commctrl.h>
#include <msi.h>

// WiX 7 headers use the original Microsoft SAL annotation names.
#ifdef __MINGW32__
#define __in _In_
#define __in_z _In_z_
#define __in_z_opt _In_opt_z_
#define __out _Out_
#define __out_ecount_opt(n) _Out_writes_opt_(n)
#endif

#include <BootstrapperApplicationTypes.h>
#include <BAFunctions.h>

static HWND setupWindow;
static bool interactive;
static bool raisedForElevation;

static HRESULT WINAPI OnMessage(BA_FUNCTIONS_MESSAGE message, const LPVOID args, LPVOID, LPVOID)
{
    if (!interactive) return S_OK;
    constexpr UINT positionFlags = SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE;
    switch (message)
    {
    case BA_FUNCTIONS_MESSAGE_ONTHEMELOADED:
        setupWindow = static_cast<const BA_FUNCTIONS_ONTHEMELOADED_ARGS*>(args)->hWnd;
        break;
    case BA_FUNCTIONS_MESSAGE_ONELEVATEBEGIN:
        // Explorer can reclaim the foreground when Windows switches back from UAC.
        // Keep the interactive setup above it for the duration of this handoff.
        if (setupWindow && GetForegroundWindow() == setupWindow && !IsIconic(setupWindow)
            && !(GetWindowLongPtrW(setupWindow, GWL_EXSTYLE) & WS_EX_TOPMOST))
        {
            raisedForElevation = SetWindowPos(setupWindow, HWND_TOPMOST, 0, 0, 0, 0, positionFlags) != FALSE;
        }
        break;
    case BA_FUNCTIONS_MESSAGE_ONELEVATECOMPLETE:
        if (raisedForElevation)
        {
            raisedForElevation = false;
            if (SUCCEEDED(static_cast<const BA_ONELEVATECOMPLETE_ARGS*>(args)->hrStatus) && !IsIconic(setupWindow))
            {
                SetForegroundWindow(setupWindow);
            }
            SetWindowPos(setupWindow, HWND_NOTOPMOST, 0, 0, 0, 0, positionFlags);
        }
        break;
    default:
        break;
    }
    return S_OK;
}

extern "C" __declspec(dllexport) HRESULT WINAPI BAFunctionsCreate(
    const BA_FUNCTIONS_CREATE_ARGS* args, BA_FUNCTIONS_CREATE_RESULTS* results)
{
    interactive = args->pCommand->display == BOOTSTRAPPER_DISPLAY_FULL;
    results->pfnBAFunctionsProc = OnMessage;
    return S_OK;
}
