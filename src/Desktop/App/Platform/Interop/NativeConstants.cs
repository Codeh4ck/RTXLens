namespace RTXLens.App.Platform.Interop;

internal static class NativeConstants
{
    public const int WindowProcedureIndex = -4;
    public const uint WindowSize = 0x0005;
    public const uint ContextMenu = 0x007B;
    public const uint LeftDoubleClick = 0x0203;
    public const uint RightButtonUp = 0x0205;
    public const uint TraySelect = 0x0400;
    public const uint TrayKeyboardSelect = 0x0401;
    public const uint BalloonClick = 0x0405;
    public const uint NotifyAdd = 0;
    public const uint NotifyModify = 1;
    public const uint NotifyDelete = 2;
    public const uint NotifySetVersion = 4;
    public const uint NotifyMessageFlag = 0x01;
    public const uint NotifyIconFlag = 0x02;
    public const uint NotifyTipFlag = 0x04;
    public const uint NotifyInfoFlag = 0x10;
    public const uint NotifyGuidFlag = 0x20;
    public const uint NotifyShowTipFlag = 0x80;
    public const uint BalloonWarning = 2;
    public const uint RespectQuietTime = 0x80;
    public const uint IconImage = 1;
    public const uint LoadFromFile = 0x10;
    public const uint MenuSeparator = 0x0800;
    public const uint MenuRightButton = 0x0002;
    public const uint MenuReturnCommand = 0x0100;
    public const uint CommandOpen = 100;
    public const uint CommandSettings = 101;
    public const uint CommandExit = 102;
}
