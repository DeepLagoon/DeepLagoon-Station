using Robust.Client.Input;

namespace Content.Client._DeepLagoon.WebUI;

public sealed partial class TguiPanel
{
    /// <summary>Decorative feedback only. Never captures or synthesizes gameplay input.</summary>
    public bool ConsoleKeyboardFeedback { get; set; }
    private readonly HashSet<string> _consoleKeys = new();

    private void OnConsoleKey(KeyEventArgs args, KeyEventType type)
    {
        if (!ConsoleKeyboardFeedback || _input == null || !Web.IsReady || UsingNative) return;
        var code = ConsoleKeyCode(args.Key);
        if (code == null || type == KeyEventType.Repeat) return;
        if (type == KeyEventType.Up)
        {
            if (_consoleKeys.Remove(code)) SendConsoleKey(code, false);
            return;
        }
        if (!VisibleInTree || FloatingWindow() is not { } window || !window.IsAtFront()) return;
        var inside = Web.HasInputFocus;
        // The native radar is a sibling of the browser. Observe its clicks too.
        for (var hit = UserInterfaceManager.MouseGetControl(_input.MouseScreenPosition); hit != null; hit = hit.Parent)
        {
            if (hit != window) continue;
            inside = true;
            break;
        }
        if (inside && _consoleKeys.Add(code)) SendConsoleKey(code, true);
    }

    private void SendConsoleKey(string code, bool down) => Web.Send("console/key",
        new TguiData().String("code", code).Bool("down", down).ToString());

    private void ClearConsoleKeys()
    {
        foreach (var code in _consoleKeys) SendConsoleKey(code, false);
        _consoleKeys.Clear();
    }

    internal static string? ConsoleKeyCode(Keyboard.Key key)
    {
        var name = key.ToString();
        if (name.Length == 1 && name[0] is >= 'A' and <= 'Z') return "Key" + name;
        // Keep both operands as strings: concatenating a char can lower to a
        // ReadOnlySpan<char>(ref char) constructor forbidden by the sandbox.
        if (name.Length == 4 && name.StartsWith("Num", StringComparison.Ordinal) && char.IsAsciiDigit(name[3])) return "Digit" + name.Substring(3);
        return key switch
        {
            Keyboard.Key.MouseLeft => "Pointer", Keyboard.Key.Return => "Enter",
            Keyboard.Key.Left => "ArrowLeft", Keyboard.Key.Right => "ArrowRight",
            Keyboard.Key.Up => "ArrowUp", Keyboard.Key.Down => "ArrowDown",
            Keyboard.Key.Shift => "ShiftLeft", Keyboard.Key.Control => "ControlLeft", Keyboard.Key.Alt => "AltLeft",
            Keyboard.Key.Escape or Keyboard.Key.Tab or Keyboard.Key.Space or Keyboard.Key.BackSpace or
                Keyboard.Key.Minus or Keyboard.Key.Equal => name == "BackSpace" ? "Backspace" : name,
            _ => null,
        };
    }
}
