using System.Text;

namespace FarmaFlow.Migration;

internal static class ProcessSecretReader
{
    private static int _secretIndex;

    internal static string Read(string prompt)
    {
        Console.Write(prompt);
        string variable = $"FARMAFLOW_SECRET_{Interlocked.Increment(ref _secretIndex)}";
        string? supplied = Environment.GetEnvironmentVariable(variable);
        if (supplied is not null)
        {
            Environment.SetEnvironmentVariable(variable, null);
            Console.WriteLine();
            return supplied;
        }

        if (Console.IsInputRedirected)
            return Console.ReadLine() ?? string.Empty;

        var result = new StringBuilder();
        while (true)
        {
            ConsoleKeyInfo key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter) break;
            if (key.Key == ConsoleKey.Backspace && result.Length > 0) result.Length--;
            else if (!char.IsControl(key.KeyChar)) result.Append(key.KeyChar);
        }
        Console.WriteLine();
        return result.ToString();
    }
}
