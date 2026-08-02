using System.Diagnostics;
using CopyTool.Core;

// Benchmark harness for the engine. No UI, no shell integration — just numbers,
// so engine changes can be judged before anything is wired up.
//
//   CopyTool.Bench profile <path>
//   CopyTool.Bench scan    <source> [<source> ...]
//   CopyTool.Bench copy    <source> <destination-folder>
//   CopyTool.Bench compare <source> <destination-folder>   (engine vs robocopy)

if (args.Length < 2)
{
    Console.WriteLine("""
        CopyTool.Bench

          profile <path>                        volume geometry and chosen defaults
          scan    <source> [...]                walk the tree, print the histogram
          copy    <source> <destination> [--background]   run the engine
          compare <source> <destination>        engine vs robocopy on the same input
          conflict <workdir>                    run every conflict policy over one fixture
          elevation <path>                      token state and whether <path> is writable
          preflight <source> <destination>      every check that runs before a copy
        """);
    return 1;
}

string verb = args[0].ToLowerInvariant();

try
{
    switch (verb)
    {
        case "profile": Profile(args[1]); break;
        case "scan":    ScanOnly(args[1..]); break;
        case "copy":
            await Copy(args[1], args[2], args.Contains("--background"), args.Contains("--overwrite"));
            break;
        case "compare": await Compare(args[1], args[2]); break;
        case "conflict": await ConflictMatrix(args[1]); break;
        case "elevation": ElevationReport(args[1]); break;
        case "preflight": PreflightReport(args[1], args[2]); break;
        case "whoislocking":
        {
            IReadOnlyList<string> holders = LockFinder.WhoIsUsing(args[1]);
            Console.WriteLine(holders.Count == 0
                ? "nothing is holding it open"
                : $"held by: {string.Join(", ", holders)}");
            break;
        }
        default:
            Console.Error.WriteLine($"unknown verb: {verb}");
            return 1;
    }
}
catch (Exception e)
{
    Console.Error.WriteLine($"ERROR: {e.Message}");
    return 1;
}

return 0;

static void Profile(string path)
{
    VolumeProfile p = VolumeProfiler.ForPath(path);
    Console.WriteLine($"volume            {p.Root}   ({p.Id})");
    Console.WriteLine($"filesystem        {p.FileSystem}");
    Console.WriteLine($"media / bus       {p.Media} / {p.Bus}");
    Console.WriteLine($"sector / cluster  {p.BytesPerSector} / {p.ClusterSize}");
    Console.WriteLine($"free              {Bytes(p.FreeBytes)}");
    Console.WriteLine();
    Console.WriteLine($"large threshold   {Bytes(p.LargeFileThreshold)}");
    Console.WriteLine($"initial workers   {p.InitialWorkers}");
    Console.WriteLine($"queue depth       {p.QueueDepth}");
    Console.WriteLine($"chunk size        {Bytes(p.ChunkSize)}");
    Console.WriteLine($"block cloning     {p.SupportsBlockCloning}");
}

static void ScanOnly(string[] sources)
{
    var sw = Stopwatch.StartNew();
    ScanResult r = Scanner.Scan(sources);
    sw.Stop();

    var (tiny, small, medium, large) = r.Histogram;
    Console.WriteLine($"scanned in        {sw.ElapsedMilliseconds} ms");
    Console.WriteLine($"files / dirs      {r.FileCount:N0} / {r.Directories.Count:N0}");
    Console.WriteLine($"total             {Bytes(r.TotalBytes)}");
    Console.WriteLine($"  <64 KB          {tiny:N0}");
    Console.WriteLine($"  64 KB - 1 MB    {small:N0}");
    Console.WriteLine($"  1 MB - 64 MB    {medium:N0}");
    Console.WriteLine($"  >64 MB          {large:N0}");
    if (r.SkippedReparsePoints.Count > 0) Console.WriteLine($"reparse points    {r.SkippedReparsePoints.Count} (not followed)");
    if (r.Inaccessible.Count > 0)         Console.WriteLine($"inaccessible      {r.Inaccessible.Count}");
}

static async Task Copy(string source, string destination, bool background = false, bool overwrite = false)
{
    ScanResult scan = Scanner.Scan([source]);
    Console.WriteLine($"{scan.FileCount:N0} files, {Bytes(scan.TotalBytes)}" +
                      (background ? "   [background I/O priority]" : "") +
                      (overwrite ? "   [conflict=overwrite]" : ""));

    var lastLine = 0;
    var start = Stopwatch.StartNew();
    int lastWorkers = -1;
    var trajectory = new List<(double Seconds, int Workers, double Rate)>();

    var engine = new CopyEngine
    {
        Policies = new JobPolicies
        {
            BackgroundIo = background,
            Conflict = overwrite ? ConflictPolicy.Overwrite : ConflictPolicy.Ask,
        },
        Progress = new Progress<CopyProgress>(p =>
        {
            // Record every worker-count change so the controller's decisions are
            // visible instead of inferred.
            if (p.Workers != lastWorkers)
            {
                lastWorkers = p.Workers;
                trajectory.Add((start.Elapsed.TotalSeconds, p.Workers, p.BytesPerSecond));
            }

            if (Environment.TickCount - lastLine < 250) return;
            lastLine = Environment.TickCount;
            double pct = p.BytesTotal > 0 ? 100.0 * p.BytesDone / p.BytesTotal : 0;
            Console.Write($"\r  {pct,5:F1}%  {Bytes(p.BytesDone)} / {Bytes(p.BytesTotal)}  " +
                          $"{Bytes((long)p.BytesPerSecond)}/s  workers={p.Workers}   ");
        }),
    };

    CopyReport report = await engine.RunAsync(scan, destination, CopyOperation.Copy);
    Console.WriteLine();
    PrintReport(report);

    if (trajectory.Count > 1)
    {
        Console.WriteLine();
        Console.WriteLine("controller trajectory (worker count vs measured rate):");
        foreach (var (sec, workers, rate) in trajectory)
            Console.WriteLine($"  t={sec,6:F2}s  workers={workers,2}  {Bytes((long)rate),10}/s");
    }
}

static async Task Compare(string source, string destination)
{
    string engineDest = Path.Combine(destination, "_bench_engine");
    string roboDest   = Path.Combine(destination, "_bench_robocopy");

    ScanResult scan = Scanner.Scan([source]);
    var (tiny, small, medium, large) = scan.Histogram;
    Console.WriteLine($"input   {scan.FileCount:N0} files, {Bytes(scan.TotalBytes)}  " +
                      $"(tiny={tiny} small={small} medium={medium} large={large})");
    Console.WriteLine($"source  {VolumeProfiler.ForPath(source)}");
    Console.WriteLine($"dest    {VolumeProfiler.ForPath(destination)}");
    Console.WriteLine();

    Reset(engineDest);
    var engine = new CopyEngine();
    CopyReport mine = await engine.RunAsync(scan, engineDest, CopyOperation.Copy);
    Console.WriteLine($"CopyTool   {mine.Elapsed.TotalSeconds,7:F2} s   {Bytes((long)mine.BytesPerSecond),10}/s");
    if (mine.Failures.Count > 0) Console.WriteLine($"           {mine.Failures.Count} failures");

    Reset(roboDest);
    var sw = Stopwatch.StartNew();
    RunRobocopy(source, roboDest);
    sw.Stop();
    double roboRate = scan.TotalBytes / sw.Elapsed.TotalSeconds;
    Console.WriteLine($"robocopy   {sw.Elapsed.TotalSeconds,7:F2} s   {Bytes((long)roboRate),10}/s");

    Console.WriteLine();
    double ratio = sw.Elapsed.TotalSeconds / mine.Elapsed.TotalSeconds;
    Console.WriteLine(ratio >= 1
        ? $"=> CopyTool is {ratio:F2}x faster"
        : $"=> robocopy is {1 / ratio:F2}x faster");

    Reset(engineDest);
    Reset(roboDest);
}

static void Reset(string dir)
{
    if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
}

/// <summary>
/// Runs every conflict policy over one fixture and prints what happened to each
/// file. The fixture covers the three cases that actually differ: byte-identical,
/// destination older/smaller, destination newer/larger.
/// </summary>
static async Task ConflictMatrix(string workDir)
{
    string src = Path.Combine(workDir, "src");
    Reset(workDir);
    Directory.CreateDirectory(src);

    var stamp = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    // identical: same bytes, same timestamp as the destination will have
    File.WriteAllText(Path.Combine(src, "identical.txt"), "same content");
    File.SetLastWriteTimeUtc(Path.Combine(src, "identical.txt"), stamp);

    // source is newer and larger than the destination copy
    File.WriteAllText(Path.Combine(src, "src-newer.txt"), "source is longer than dest");
    File.SetLastWriteTimeUtc(Path.Combine(src, "src-newer.txt"), stamp.AddHours(2));

    // source is older and smaller than the destination copy
    File.WriteAllText(Path.Combine(src, "src-older.txt"), "short");
    File.SetLastWriteTimeUtc(Path.Combine(src, "src-older.txt"), stamp.AddHours(-2));

    void SeedDestination(string dst)
    {
        Reset(dst);
        Directory.CreateDirectory(Path.Combine(dst, "src"));
        string d = Path.Combine(dst, "src");

        File.WriteAllText(Path.Combine(d, "identical.txt"), "same content");
        File.SetLastWriteTimeUtc(Path.Combine(d, "identical.txt"), stamp);

        File.WriteAllText(Path.Combine(d, "src-newer.txt"), "old");
        File.SetLastWriteTimeUtc(Path.Combine(d, "src-newer.txt"), stamp);

        File.WriteAllText(Path.Combine(d, "src-older.txt"), "destination is much longer");
        File.SetLastWriteTimeUtc(Path.Combine(d, "src-older.txt"), stamp);
    }

    Console.WriteLine($"{"policy",-12} {"identical.txt",-26} {"src-newer.txt",-26} {"src-older.txt",-26} pend skip");
    Console.WriteLine(new string('-', 104));

    foreach (ConflictPolicy policy in Enum.GetValues<ConflictPolicy>())
    {
        string dst = Path.Combine(workDir, "dst");
        SeedDestination(dst);

        ScanResult scan = Scanner.Scan([src]);
        var engine = new CopyEngine { Policies = new JobPolicies { Conflict = policy } };
        CopyReport report = await engine.RunAsync(scan, dst, CopyOperation.Copy);

        string d = Path.Combine(dst, "src");
        Console.WriteLine($"{policy,-12} {Describe(d, "identical.txt"),-26} " +
                          $"{Describe(d, "src-newer.txt"),-26} {Describe(d, "src-older.txt"),-26} " +
                          $"{report.Pending.Count,4} {report.Skipped.Count,4}");
    }

    Console.WriteLine();
    Console.WriteLine("legend: <content> = what the destination holds afterwards; +N = N extra 'name (2)' files");
    Reset(workDir);
}

/// <summary>
/// Reports the token state and whether a path is writable. Run it both elevated
/// and not: the probe must disagree between the two, or it is not measuring
/// anything.
/// </summary>
static void ElevationReport(string path)
{
    Console.WriteLine($"elevated          {Elevation.IsElevated}");
    Console.WriteLine($"can elevate       {Elevation.CanElevate}");
    Console.WriteLine($"UAC disabled      {Elevation.UacDisabled}");
    Console.WriteLine();
    Console.WriteLine($"path              {path}");
    Console.WriteLine($"can write         {Elevation.CanWriteTo(path)}");
    Console.WriteLine($"can read          {(File.Exists(path) || Directory.Exists(path) ? Elevation.CanRead(path).ToString() : "n/a")}");
}

static void PreflightReport(string source, string destination)
{
    ScanResult scan = Scanner.Scan([source]);
    PreflightResult result = Preflight.Run([source], destination, scan, CopyOperation.Copy);

    Console.WriteLine($"source       {source}");
    Console.WriteLine($"destination  {destination}  ({VolumeProfiler.ForPath(destination).FileSystem})");
    Console.WriteLine($"payload      {scan.FileCount:N0} files, {Bytes(scan.TotalBytes)}");
    if (scan.PlaceholderCount > 0)
        Console.WriteLine($"cloud-only   {scan.PlaceholderCount:N0} files, {Bytes(scan.PlaceholderBytes)}");
    Console.WriteLine();

    if (result.Issues.Count == 0)
    {
        Console.WriteLine("no issues");
        return;
    }

    // The bench prints codes, not sentences: Core no longer composes prose, and a
    // diagnostic tool wants the code anyway.
    foreach (PreflightIssue issue in result.Issues)
    {
        var details = new List<string>();
        if (issue.Count > 0) details.Add($"count={issue.Count}");
        if (issue.Bytes > 0) details.Add($"bytes={Bytes(issue.Bytes)}");
        if (issue.OtherBytes > 0) details.Add($"free={Bytes(issue.OtherBytes)}");
        if (issue.FileSystem is not null) details.Add($"fs={issue.FileSystem}");
        if (issue.Path is not null) details.Add($"path={issue.Path}");

        Console.WriteLine($"[{issue.Severity,-8}] {issue.Code,-28} {string.Join("  ", details)}");
    }

    Console.WriteLine();
    Console.WriteLine(result.CanProceed ? "=> would proceed" : "=> BLOCKED");
}

static string Describe(string dir, string name)
{
    string path = Path.Combine(dir, name);
    if (!File.Exists(path)) return "MISSING";

    string text = File.ReadAllText(path);
    if (text.Length > 14) text = text[..14] + "…";

    string stem = Path.GetFileNameWithoutExtension(name);
    int extras = Directory.GetFiles(dir, $"{stem} (*)*").Length;
    return extras > 0 ? $"{text} +{extras}" : text;
}

static void RunRobocopy(string source, string destination)
{
    bool isFile = File.Exists(source);
    string args = isFile
        ? $"\"{Path.GetDirectoryName(source)}\" \"{destination}\" \"{Path.GetFileName(source)}\" /J /NFL /NDL /NJH /NJS /NP"
        : $"\"{source}\" \"{destination}\" /E /J /MT:8 /NFL /NDL /NJH /NJS /NP";

    var psi = new ProcessStartInfo("robocopy.exe", args) { UseShellExecute = false, CreateNoWindow = true };
    using Process? p = Process.Start(psi);
    p?.WaitForExit();
}

static void PrintReport(CopyReport r)
{
    Console.WriteLine($"strategy   {r.Strategy}");
    Console.WriteLine($"copied     {r.FilesCopied:N0} files, {Bytes(r.BytesCopied)}");
    Console.WriteLine($"elapsed    {r.Elapsed.TotalSeconds:F2} s   ({Bytes((long)r.BytesPerSecond)}/s)");
    Console.WriteLine($"skipped    {r.Skipped.Count}");
    Console.WriteLine($"pending    {r.Pending.Count}");
    Console.WriteLine($"needs-elev {r.NeedsElevation.Count()}   <- permission questions, not failures");
    if (r.Failures.Count > 0)
    {
        Console.WriteLine($"failures   {r.Failures.Count}");
        foreach (CopyFailure f in r.Failures.Take(10)) Console.WriteLine($"  {f.Source}: {f.Reason}");
    }
}

static string Bytes(long n) => Format.Bytes(n);
