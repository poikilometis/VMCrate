using System.Text;

namespace VMCrate.Tests;

internal static partial class LiveAcceptance
{
    private sealed record WindowsGuestCredentials(string User, string Password);

    private static WindowsGuestCredentials ReadWindowsGuestCredentials()
    {
        if (Console.IsInputRedirected)
            throw new InvalidOperationException("The Windows guest check requires an interactive test terminal.");

        Console.Write("Test Windows guest user: ");
        var user = Console.ReadLine()?.Trim();
        if (string.IsNullOrWhiteSpace(user))
            throw new InvalidOperationException("Supply a test Windows guest user.");
        Console.Write("Test Windows guest password (hidden): ");
        var password = new StringBuilder();
        for (;;)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
                break;
            if (key.Key == ConsoleKey.Backspace && password.Length != 0)
                password.Length--;
            else if (!char.IsControl(key.KeyChar))
                password.Append(key.KeyChar);
        }
        Console.WriteLine();
        if (password.Length == 0)
            throw new InvalidOperationException("Supply a test Windows guest password.");
        return new(user, password.ToString());
    }
}
