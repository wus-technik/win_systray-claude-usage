using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace ClaudeUsageTraySetupStub;

/// <summary>Win32 task dialogs via P/Invoke: radio buttons and a progress bar are native features, so
/// no WinForms is needed and the exe stays small. Everything here is layout; every string shown is
/// decided elsewhere.</summary>
internal static unsafe class Wizard
{
    public const string Title = "Claude Usage Tray Setup";

    private const int RadioStable = 100;
    private const int RadioBeta = 101;
    private const int IdOk = 1;
    private const int IdCancel = 2;

    // ---- public pages ----

    public static Ring? ChooseRing(Ring preselected, InstallInfo? installed, Ring? currentRing)
    {
        var content = installed is null
            ? $"{Rings.ProductName} will be installed for the current user only; no administrator rights are needed.\n\nChoose which releases to follow."
            : $"{Rings.ProductName} {installed.Version} is installed on the {(currentRing == Ring.Beta ? "beta" : "stable")} ring.\n\nChoose which releases it should follow.";

        var page = new Page
        {
            Instruction = "Choose a release ring",
            Content = content,
            Icon = Icon.None,
            RadioButtons = [(RadioStable, "Stable (recommended)"), (RadioBeta, "Beta (pre-release builds)")],
            DefaultRadio = preselected == Ring.Beta ? RadioBeta : RadioStable,
            Buttons = [(IdOk, "Continue")],
            CommonButtons = CommonButtons.Cancel,
        };
        var (button, radio) = Show(page, callback: null);
        if (button != IdOk) return null;
        return radio == RadioBeta ? Ring.Beta : Ring.Stable;
    }

    /// <summary>Runs <paramref name="work"/> on the thread pool while a modal progress dialog shows.
    /// Cancel from the dialog cancels the token; the work's result is awaited either way.</summary>
    public static bool? RunWithProgress(string instruction, string content, Func<IProgress<double>, CancellationToken, Task<bool>> work)
    {
        using var cts = new CancellationTokenSource();
        var state = ProgressState.Reset();
        var progress = new Progress<double>(fraction => state.Percent = (int)Math.Round(Math.Clamp(fraction, 0, 1) * 100));
        var task = Task.Run(() => work(progress, cts.Token), CancellationToken.None);
        task.ContinueWith(_ => state.Done = true, TaskScheduler.Default);

        var page = new Page
        {
            Instruction = instruction,
            Content = content,
            Icon = Icon.None,
            CommonButtons = CommonButtons.Cancel,
            ShowProgressBar = true,
            CallbackTimer = true,
        };
        var (button, _) = Show(page, callback: &ProgressCallback);

        if (!state.Done && button == IdCancel)
        {
            cts.Cancel();
            try { task.GetAwaiter().GetResult(); } catch (OperationCanceledException) { }
            return null;
        }
        try { return task.GetAwaiter().GetResult(); }
        catch (OperationCanceledException) { return null; }
    }

    public static void Info(string instruction, string content) => Show(new Page { Instruction = instruction, Content = content, Icon = Icon.Information, CommonButtons = CommonButtons.Ok }, null);
    public static void Warning(string instruction, string content) => Show(new Page { Instruction = instruction, Content = content, Icon = Icon.Warning, CommonButtons = CommonButtons.Ok }, null);
    public static void Error(string instruction, string content) => Show(new Page { Instruction = instruction, Content = content, Icon = Icon.Error, CommonButtons = CommonButtons.Ok }, null);

    // ---- progress callback state (one dialog at a time; the callback is static) ----

    private sealed class ProgressState
    {
        public static readonly ProgressState Current = new();
        public volatile int Percent;
        public volatile bool Done;
        public IntPtr Hwnd;
        public static ProgressState Reset() { Current.Percent = 0; Current.Done = false; Current.Hwnd = IntPtr.Zero; return Current; }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int ProgressCallback(IntPtr hwnd, uint notification, nuint wParam, nint lParam, nint refData)
    {
        var state = ProgressState.Current;
        switch (notification)
        {
            case TdnCreated:
                state.Hwnd = hwnd;
                SendMessageW(hwnd, TdmSetProgressBarRange, 0, (nint)(100 << 16)); // MAKELPARAM(0, 100)
                break;
            case TdnTimer:
                SendMessageW(hwnd, TdmSetProgressBarPos, (nuint)state.Percent, 0);
                // Close ourselves when the work finished; the caller tells "done" from "cancelled" by state.Done.
                if (state.Done) SendMessageW(hwnd, TdmClickButton, IdCancel, 0);
                break;
        }
        return 0; // S_OK
    }

    // ---- TaskDialogIndirect plumbing ----

    private enum Icon : ushort { None = 0, Warning = 0xFFFF, Error = 0xFFFE, Information = 0xFFFD }

    [Flags]
    private enum CommonButtons : uint { None = 0, Ok = 0x1, Cancel = 0x8 }

    private sealed class Page
    {
        public string Instruction = "";
        public string Content = "";
        public Icon Icon;
        public CommonButtons CommonButtons;
        public (int Id, string Text)[] Buttons = [];
        public (int Id, string Text)[] RadioButtons = [];
        public int DefaultRadio;
        public bool ShowProgressBar;
        public bool CallbackTimer;
    }

    private const uint TdfAllowDialogCancellation = 0x8;
    private const uint TdfShowProgressBar = 0x200;
    private const uint TdfCallbackTimer = 0x800;
    private const uint TdfPositionRelativeToWindow = 0x1000;
    private const uint TdfSizeToContent = 0x1000000;

    private const uint TdnCreated = 0;
    private const uint TdnTimer = 4;

    private const uint WmUser = 0x0400;
    private const uint TdmClickButton = WmUser + 102;
    private const uint TdmSetProgressBarRange = WmUser + 105;
    private const uint TdmSetProgressBarPos = WmUser + 106;

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct TaskDialogButton
    {
        public int Id;
        public IntPtr Text;
    }

    /// <summary>TASKDIALOGCONFIG is declared with #pragma pack(1); cbSize must be 160 on x64.</summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct TaskDialogConfig
    {
        public uint cbSize;
        public IntPtr hwndParent;
        public IntPtr hInstance;
        public uint dwFlags;
        public uint dwCommonButtons;
        public IntPtr pszWindowTitle;
        public IntPtr hMainIcon;
        public IntPtr pszMainInstruction;
        public IntPtr pszContent;
        public uint cButtons;
        public IntPtr pButtons;
        public int nDefaultButton;
        public uint cRadioButtons;
        public IntPtr pRadioButtons;
        public int nDefaultRadioButton;
        public IntPtr pszVerificationText;
        public IntPtr pszExpandedInformation;
        public IntPtr pszExpandedControlText;
        public IntPtr pszCollapsedControlText;
        public IntPtr hFooterIcon;
        public IntPtr pszFooter;
        public IntPtr pfCallback;
        public IntPtr lpCallbackData;
        public uint cxWidth;
    }

    [DllImport("comctl32.dll", ExactSpelling = true)]
    private static extern int TaskDialogIndirect(ref TaskDialogConfig config, out int button, out int radioButton, out int verificationChecked);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern nint SendMessageW(IntPtr hwnd, uint message, nuint wParam, nint lParam);

    private static (int Button, int Radio) Show(Page page, delegate* unmanaged[Stdcall]<IntPtr, uint, nuint, nint, nint, int> callback)
    {
        var allocations = new List<IntPtr>();
        IntPtr Str(string s) { var p = Marshal.StringToHGlobalUni(s); allocations.Add(p); return p; }
        IntPtr ButtonArray((int Id, string Text)[] buttons)
        {
            if (buttons.Length == 0) return IntPtr.Zero;
            var block = Marshal.AllocHGlobal(buttons.Length * sizeof(TaskDialogButton));
            allocations.Add(block);
            var items = (TaskDialogButton*)block;
            for (var i = 0; i < buttons.Length; i++) items[i] = new TaskDialogButton { Id = buttons[i].Id, Text = Str(buttons[i].Text) };
            return block;
        }

        try
        {
            var flags = TdfAllowDialogCancellation | TdfPositionRelativeToWindow | TdfSizeToContent;
            if (page.ShowProgressBar) flags |= TdfShowProgressBar;
            if (page.CallbackTimer) flags |= TdfCallbackTimer;

            var config = new TaskDialogConfig
            {
                cbSize = (uint)sizeof(TaskDialogConfig),
                dwFlags = flags,
                dwCommonButtons = (uint)page.CommonButtons,
                pszWindowTitle = Str(Title),
                hMainIcon = (IntPtr)(ushort)page.Icon,
                pszMainInstruction = Str(page.Instruction),
                pszContent = Str(page.Content),
                cButtons = (uint)page.Buttons.Length,
                pButtons = ButtonArray(page.Buttons),
                cRadioButtons = (uint)page.RadioButtons.Length,
                pRadioButtons = ButtonArray(page.RadioButtons),
                nDefaultRadioButton = page.DefaultRadio,
                pfCallback = (IntPtr)callback,
            };
            var hr = TaskDialogIndirect(ref config, out var button, out var radio, out _);
            // A failed call (no comctl32 v6, unlikely with the manifest) reads as cancel: nothing was chosen.
            return hr < 0 ? (IdCancel, 0) : (button, radio);
        }
        finally
        {
            foreach (var p in allocations) Marshal.FreeHGlobal(p);
        }
    }
}
