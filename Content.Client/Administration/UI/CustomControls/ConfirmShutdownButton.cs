using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;

namespace Content.Client.Administration.UI.CustomControls;

public sealed class ConfirmShutdownButton : CommandButton
{
    private int _step;

    protected override void Execute(ButtonEventArgs obj)
    {
        _step++;

        switch (_step)
        {
            case 1:
                Text = Loc.GetString("server-shutdown-confirm-1");
                break;
            case 2:
                Text = Loc.GetString("server-shutdown-confirm-2");
                ModulateSelfOverride = Color.Red;
                break;
            default:
                base.Execute(obj);
                break;
        }
    }
}
