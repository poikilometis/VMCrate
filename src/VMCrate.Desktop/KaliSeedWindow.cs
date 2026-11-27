using System.Windows;
using System.Windows.Controls;
using VMCrate.Core;
using VMCrate.Windows;

namespace VMCrate.Desktop;

internal sealed class KaliSeedWindow : WorkflowWindow
{
    internal KaliSeedWindow(Labels text, string source, string expectedHash) : base(text, "kali.seedAction", 640, 470)
    {
        _ = LocalPaths.RequireFile(source, ".iso");
        Note(Text["kali.seedHint"]);
        var username = Field("kali.user", new TextBox { Text = "kali", MaxLength = 31, FlowDirection = FlowDirection.LeftToRight });
        var password = Field("kali.password", new PasswordBox { MaxLength = 128 });
        Action(Actions(), "kali.seedAction", () => WithCancellationAsync(async token =>
        {
            if (SaveFile("kali.seedAction", "vmcrate-kali-setup.iso", ".iso") is not { } target)
                return;
            var user = username.Text;
            var secret = password.Password;
            Digest = await Task.Run(() => KaliSetupSeed.CreateAsync(source, expectedHash, target, user, secret, token), token);
            Destination = target;
            password.Clear();
            FinishDialog();
        }), true);
    }

    internal string? Destination { get; private set; }
    internal string? Digest { get; private set; }
}
