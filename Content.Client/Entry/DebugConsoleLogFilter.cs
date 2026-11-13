using System.Threading;
using Content.Client._DeepLagoon.WebUI;
using Content.Shared._DeepLagoon.WebUI;
using Robust.Client.Console;
using Robust.Client.UserInterface.Controls;
using Robust.Shared.Configuration;
using Robust.Shared.Log;
using Robust.Shared.Utility;

namespace Content.Client.Entry;

internal sealed class DebugConsoleLogFilter : IDisposable
{
    private const string PackagedWebRoot = "res://deeplagoon/Web/DeepLagoon/";

    private readonly IClientConsoleHost _host;
    private readonly OutputPanel _output;
    private readonly IConfigurationManager _config;
    private string? _devOrigin;
    private int _scanRequested = 1;
    private bool _scanNextFrame;

    public DebugConsoleLogFilter(IClientConsoleHost host, OutputPanel output, IConfigurationManager config)
    {
        _host = host;
        _output = output;
        _config = config;
        _config.OnValueChanged(WebUiCVars.DevServer, UpdateDevOrigin, true);
        _host.AddFormatted += OnFormatted;
    }

    private void UpdateDevOrigin(string configured)
    {
        Interlocked.Exchange(ref _devOrigin, TguiDevelopmentPolicy.GetOrigin(configured));
    }

    private void OnFormatted(object? sender, AddFormattedMessageArgs args)
    {
        if (IsHiddenLog(args.Message, Interlocked.CompareExchange(ref _devOrigin, null, null)))
            Interlocked.Exchange(ref _scanRequested, 1);
    }

    public void FilterOutput()
    {
        if (!_output.VisibleInTree)
            return;

        // The stock console drains its queue during the UI update. A producer
        // can request a scan after that drain, so also scan the following frame.
        var requested = Interlocked.Exchange(ref _scanRequested, 0) != 0;
        if (requested || _scanNextFrame)
        {
            for (var i = _output.EntryCount - 1; i >= 0; i--)
            {
                if (IsHiddenLog(_output.GetMessage(i), _devOrigin))
                    _output.RemoveEntry(i);
            }
        }

        _scanNextFrame = requested;
    }

    public void Dispose()
    {
        _host.AddFormatted -= OnFormatted;
        _config.UnsubValueChanged(WebUiCVars.DevServer, UpdateDevOrigin);
    }

    internal static bool IsHiddenLog(FormattedMessage message, string? devOrigin = null)
    {
        // DebugConsoleLogHandler emits a colored "[LEVEL] sawmill: message".
        if (message.Count < 8 || message[1].Value.StringValue != "[" ||
            message[5].Value.StringValue is not { } source ||
            !source.StartsWith("] ", StringComparison.Ordinal) || !source.EndsWith(": ", StringComparison.Ordinal))
            return false;

        var sawmill = source.AsSpan(2, source.Length - 4);
        var level = message[3].Value.StringValue;
        if (sawmill.SequenceEqual("tgui") || sawmill.StartsWith("tgui.", StringComparison.Ordinal))
            return true;

#if RELEASE || FULL_RELEASE
        if (sawmill.SequenceEqual("loc") && level is LogMessage.LogNameWarning or LogMessage.LogNameError or LogMessage.LogNameFatal)
            return true;
#endif

        if (level != LogMessage.LogNameDebug || message[7].Value.StringValue is not { } body)
            return false;

        const string request = "HANDLING REQUEST: ";
        if (sawmill.SequenceEqual("root") && body.StartsWith(request, StringComparison.Ordinal))
        {
            var url = body.AsSpan(request.Length);
            if (url.StartsWith(PackagedWebRoot, StringComparison.OrdinalIgnoreCase))
                return true;

            return devOrigin != null && WikiNavigationPolicy.TryUri(body[request.Length..], out var uri) &&
                   TguiDevelopmentPolicy.IsResource(uri, devOrigin);
        }

        return sawmill.SequenceEqual("web.res") &&
               body.AsSpan().StartsWith("HANDLING: " + PackagedWebRoot, StringComparison.OrdinalIgnoreCase);
    }
}
