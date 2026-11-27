using System.Windows.Controls;
using VMCrate.Core;

namespace VMCrate.Desktop;

internal sealed class GuestProbeWindow : WorkflowWindow
{
    internal GuestProbeWindow(Labels text, LabService lab, Machine vm) : base(text, "action.guestProbe", 560, 480)
    {
        Note(vm.Name);
        Note(text["hint.guestProbe"]);
        var user = Field("label.username", new TextBox());
        var password = Field("label.password", new PasswordBox());
        var peer = Field("label.peer", new TextBox { IsEnabled = vm.Network == NetworkMode.Private });
        var result = Note("");
        Action(Actions(), "action.guestProbe", async () =>
        {
            var request = (User: user.Text, Password: password.Password, Peer: peer.Text);
            password.Clear();
            var checkedGuest = await Task.Run(() => lab.CheckGuestNetworkAsync(vm.Id, request.User, request.Password, request.Peer));
            var reached = checkedGuest.PeerReachable is bool up ? Text[up ? "probe.reached" : "probe.unreachable"] : "—";
            result.Text = Text.Format("probe.result", checkedGuest.ActiveAdapters, checkedGuest.DefaultRoutes, reached);
        }, true);
    }
}
