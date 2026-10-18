using System;
using Wildmere.World.Gen;

static class Program
{
    static int Main(string[] args)
    {
        string mode = args.Length > 0 ? args[0] : "noisestats";
        if (mode == "noisestats") { NoiseStats(); return 0; }
        Console.WriteLine("unknown mode");
        return 2;
    }

    static void NoiseStats()
    {
        var n = new Noise(12345);
        for (int oct = 1; oct <= 5; oct++)
        {
            double s = 0, s2 = 0; int cnt = 0;
            for (int i = 0; i < 400; i++)
                for (int j = 0; j < 400; j++)
                {
                    double v = n.Fbm(i * 0.137, j * 0.137, oct);
                    s += v; s2 += v * v; cnt++;
                }
            double m = s / cnt;
            Console.WriteLine($"fbm oct={oct} mean={m:F4} std={Math.Sqrt(s2 / cnt - m * m):F4}");
        }
        for (int oct = 1; oct <= 5; oct++)
        {
            double s = 0, s2 = 0; int cnt = 0; var hist = new int[10];
            for (int i = 0; i < 400; i++)
                for (int j = 0; j < 400; j++)
                {
                    double v = n.Ridged(i * 0.137, j * 0.137, oct);
                    s += v; s2 += v * v; cnt++; hist[Math.Min(9, (int)(v * 10))]++;
                }
            double m = s / cnt;
            Console.WriteLine($"ridged oct={oct} mean={m:F4} std={Math.Sqrt(s2 / cnt - m * m):F4} hist={string.Join(",", hist)}");
        }
        var sw = System.Diagnostics.Stopwatch.StartNew();
        double acc = 0;
        for (int i = 0; i < 2000000; i++) acc += n.Sample(i * 0.0137, i * 0.0071);
        Console.WriteLine($"2D sample: {sw.Elapsed.TotalMilliseconds * 1e6 / 2000000:F1} ns ({acc:F1})");
        sw.Restart();
        for (int i = 0; i < 2000000; i++) acc += n.Sample(i * 0.0137, i * 0.0071, i * 0.003);
        Console.WriteLine($"3D sample: {sw.Elapsed.TotalMilliseconds * 1e6 / 2000000:F1} ns ({acc:F1})");
    }
}
