using System.Diagnostics;

namespace Cove;

static class MemoryUsage
{
    public static long WorkingSet()
    {
        try
        {
            var ids = Family(Environment.ProcessId);
            long total = 0;
            foreach (var id in ids)
            {
                try
                {
                    using var process = Process.GetProcessById(id);
                    total += process.WorkingSet64;
                }
                catch
                {
                    // The process exited between the snapshot and the read.
                }
            }
            return total > 0 ? total : Process.GetCurrentProcess().WorkingSet64;
        }
        catch
        {
            return Process.GetCurrentProcess().WorkingSet64;
        }
    }

    static HashSet<int> Family(int root)
    {
        var children = new Dictionary<int, List<int>>();
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "/bin/ps",
            ArgumentList = { "-axo", "pid=,ppid=" },
            RedirectStandardOutput = true,
            UseShellExecute = false
        });
        var text = process?.StandardOutput.ReadToEnd() ?? "";
        process?.WaitForExit();
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) continue;
            if (!int.TryParse(parts[0], out var pid) || !int.TryParse(parts[1], out var parent)) continue;
            if (!children.TryGetValue(parent, out var list))
            {
                list = [];
                children[parent] = list;
            }
            list.Add(pid);
        }

        var ids = new HashSet<int> { root };
        var stack = new Stack<int>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var id = stack.Pop();
            if (!children.TryGetValue(id, out var kids)) continue;
            foreach (var kid in kids)
            {
                if (ids.Add(kid)) stack.Push(kid);
            }
        }
        return ids;
    }
}
