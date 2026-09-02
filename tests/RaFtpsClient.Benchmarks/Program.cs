using System.Diagnostics;
using System.Text;
using RaFtpsClient;

// Micro-benchmarks for the pure, CPU-bound parts of the library. Run with:
//   dotnet run -c Release --project tests/RaFtpsClient.Benchmarks
// Numbers are per operation after a warm-up; compare before and after a change on the same machine.

static class Program
{
    static void Run(string name, int iterations, Action body)
    {
        for (int i = 0; i < 3; i++) body();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        var stopwatch = Stopwatch.StartNew();
        for (int i = 0; i < iterations; i++) body();
        stopwatch.Stop();
        long allocatedPerOp = (GC.GetAllocatedBytesForCurrentThread() - allocatedBefore) / iterations;
        Console.WriteLine($"{name,-44} {stopwatch.Elapsed.TotalMilliseconds / iterations,9:F2} ms/op {allocatedPerOp / 1024.0,10:F0} KB/op");
    }

    static void Main()
    {
        string unixListing = BuildUnixListing(50_000);
        string windowsListing = BuildWindowsListing(50_000);
        byte[] unixBytes = Encoding.UTF8.GetBytes(unixListing);
        byte[] smallListing = Encoding.UTF8.GetBytes(unixListing.Substring(0, 3000));
        byte[] replyLines = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("226 Transfer complete, thank you very much.\r\n", 200_000)));
        string[] names = Enumerable.Range(0, 100_000).Select(i => i % 5 == 0 ? $"bad:name*{i}?.txt" : $"clean-name-{i}.txt").ToArray();

        Run("parse 50k-line Unix listing", 5, () => DirectoryListParser.GetDirectoryList(unixListing));
        Run("parse 50k-line Windows listing", 5, () => DirectoryListParser.GetDirectoryList(windowsListing));
        Run("ReadStreamAsUtf8 (5 MB listing)", 10, () => FTPSClient.ReadStreamAsUtf8(new MemoryStream(unixBytes), 8192));
        Run("ReadStreamAsUtf8 (3 KB listing)", 2000, () => FTPSClient.ReadStreamAsUtf8(new MemoryStream(smallListing), 8192));
        Run("LocalPathAllocator x 20k (10% collisions)", 1, () =>
        {
            var paths = new LocalPathAllocator();
            for (int i = 0; i < 20_000; i++) paths.Reserve("/tmp/dl/" + (i % 10 == 0 ? "dup.txt" : "file-" + i + ".txt"));
        });
        Run("PathCheck x 100k names", 3, () => { foreach (string n in names) PathCheck.GetValidLocalFileName(n); });
        Run("ControlChannelReader 200k lines", 5, () =>
        {
            var reader = new ControlChannelReader(new MemoryStream(replyLines));
            while (reader.ReadLine() != null) { }
        });
    }

    static string BuildUnixListing(int lines)
    {
        var sb = new StringBuilder();
        var rnd = new Random(42);
        for (int i = 0; i < lines; i++)
        {
            switch (i % 4)
            {
                case 0: sb.Append($"-rw-r--r-- 1 owner group {rnd.Next(1, 9_999_999)} May 31 12:{i % 60:00} file-{i}.txt\r\n"); break;
                case 1: sb.Append($"drwxr-xr-x 2 owner group 4096 Jan {1 + i % 28} 2019 dir-{i}\r\n"); break;
                case 2: sb.Append($"lrwxrwxrwx 1 owner group 11 Dec 3 09:15 link-{i} -> target-{i}\r\n"); break;
                default: sb.Append($"-rw-r--r-- 1 owner group {rnd.Next(1, 999)} Sep  1 23:59 report {i} final.pdf\r\n"); break;
            }
        }
        return sb.ToString();
    }

    static string BuildWindowsListing(int lines)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < lines; i++)
        {
            sb.Append(i % 3 == 0
                ? $"05-31-22  12:00PM       <DIR>          folder-{i}\r\n"
                : $"05-31-22  12:00PM            {i * 7} file-{i}.txt\r\n");
        }
        return sb.ToString();
    }
}
