using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;

namespace Vorticity.Tools.TestImpact;

/// <summary>Runs a child process to its end, asynchronously.</summary>
internal static class Processes
{
    /// <summary>Runs a command and returns what it wrote on its standard output.</summary>
    /// <exception cref="InvalidOperationException">The command failed.</exception>
    internal static async Task<string> CaptureAsync(string file, IEnumerable<string> arguments, string directory)
    {
        (int exit, string output) = await RunAsync(file, arguments, directory, environment: null, echo: false).ConfigureAwait(false);
        if (exit != 0)
        {
            throw new InvalidOperationException($"{file} {string.Join(' ', arguments)} exited with {exit}");
        }

        return output;
    }

    /// <summary>Runs a command, with extra environment variables, and returns its exit code and output.</summary>
    /// <param name="file">The executable.</param>
    /// <param name="arguments">Its arguments.</param>
    /// <param name="directory">The working directory.</param>
    /// <param name="environment">Variables added to the inherited environment, or null.</param>
    /// <param name="echo">Whether the output is also written to this process's own, as it comes.</param>
    internal static async Task<(int Exit, string Output)> RunAsync(
        string file, IEnumerable<string> arguments, string directory, IReadOnlyDictionary<string, string>? environment, bool echo)
    {
        ProcessStartInfo start = new ProcessStartInfo(file)
        {
            WorkingDirectory = directory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        if (environment is not null)
        {
            foreach ((string name, string value) in environment)
            {
                start.Environment[name] = value;
            }
        }

        using Process process = new Process { StartInfo = start };
        System.Text.StringBuilder output = new System.Text.StringBuilder();
        object gate = new object();
        void Collect(object sender, DataReceivedEventArgs received)
        {
            if (received.Data is null)
            {
                return;
            }

            lock (gate)
            {
                output.Append(received.Data).Append('\n');
            }

            if (echo)
            {
                Console.WriteLine(received.Data);
            }
        }

        process.OutputDataReceived += Collect;
        process.ErrorDataReceived += Collect;
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        await process.WaitForExitAsync().ConfigureAwait(false);
        lock (gate)
        {
            return (process.ExitCode, output.ToString());
        }
    }
}
