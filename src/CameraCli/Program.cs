using System.Buffers;
using System.Diagnostics;
using System.Runtime.InteropServices;
using CameraSDK;

static class Program
{
    private sealed class Options
    {
        public int Runs { get; set; } = 3;
        public double MeasureSeconds { get; set; } = 2.0;
        public double WarmupSeconds { get; set; } = 1.0;
        public Preferred4KMode Mode { get; set; } = Preferred4KMode.B_Nv12Native;
        public long? Exposure { get; set; }
        public int? Gain { get; set; }
        public int Width { get; set; } = 3840;
        public int Height { get; set; } = 2160;
        public bool SweepExposure { get; set; } = false;
        public bool SweepBrightness { get; set; } = false;
        public long SweepBrightnessStart { get; set; } = -12;
        public long SweepBrightnessEnd { get; set; } = -20; // ~1us
        public int SweepBrightnessSamples { get; set; } = 8;
        public bool DumpProcAmp { get; set; } = false;
    }

    static int Main(string[] args)
    {
        var opts = ParseArgs(args);
        using var cam = new CameraManager();

        if (!cam.SetPreferred4KMode(opts.Mode))
        {
            Console.WriteLine($"[WARN] Failed to set preferred mode ({opts.Mode}).");
        }
        if (!cam.SetPreferredResolution(opts.Width, opts.Height))
        {
            Console.WriteLine($"[WARN] Failed to set preferred resolution ({opts.Width}x{opts.Height}).");
        }

        if (!cam.Initialize())
        {
            int hr = cam.GetLastHRESULT();
            Console.WriteLine($"[ERR] Camera initialize failed. HRESULT=0x{hr:X8}");
            if (hr == unchecked((int)0x80070005))
            {
                Console.WriteLine("[HINT] Access denied. Close other camera apps/processes and check Windows camera privacy settings.");
            }
            return 1;
        }

        int nw = cam.GetNegotiatedWidth();
        int nh = cam.GetNegotiatedHeight();
        double nfps = cam.GetNegotiatedFPS();
        int nsub = cam.GetNegotiatedSubtype();
        string nsubName = nsub switch { 1 => "NV12", 2 => "MJPG", 3 => "YUY2", _ => "Unknown" };

        Console.WriteLine($"[INFO] Negotiated: {nw}x{nh} @ {nfps:F2} ({nsubName})");
        Console.WriteLine($"[INFO] Mode={opts.Mode}, Target={opts.Width}x{opts.Height}, Runs={opts.Runs}, Measure={opts.MeasureSeconds:F2}s, Warmup={opts.WarmupSeconds:F2}s");

        if (opts.Exposure.HasValue)
        {
            bool expOk = cam.SetExposure(opts.Exposure.Value);
            long expAppliedRaw = cam.GetExposure();
            int expApplied = unchecked((int)expAppliedRaw);
            Console.WriteLine($"[INFO] Exposure set req={opts.Exposure.Value}: {(expOk ? "OK" : "FAIL")}, applied={expApplied} (raw={expAppliedRaw})");
        }
        if (opts.Gain.HasValue)
        {
            bool gainOk = cam.SetGain(opts.Gain.Value);
            int gainApplied = cam.GetGain();
            Console.WriteLine($"[INFO] Gain set req={opts.Gain.Value}: {(gainOk ? "OK" : "FAIL")}, applied={gainApplied}");
        }

        long frameCount = 0;
        long byteCount = 0;
        bool measuring = false;
        bool seenFirst = false;
        var firstFrame = new ManualResetEventSlim(false);
        long startTicks = 0;

        bool lumaArmed = false;
        var lumaReady = new ManualResetEventSlim(false);
        double lumaMean = 0.0;

        cam.OnFrameReceived += (pBuffer, _, _, _, dataSize) =>
        {
            if (lumaArmed)
            {
                int sampleLen = dataSize;
                if (sampleLen > 0)
                {
                    byte[] tmp = ArrayPool<byte>.Shared.Rent(sampleLen);
                    try
                    {
                        Marshal.Copy(pBuffer, tmp, 0, sampleLen);
                        long sum = 0;
                        for (int i = 0; i < sampleLen; i++) sum += tmp[i];
                        lumaMean = (double)sum / sampleLen;
                    }
                    finally
                    {
                        ArrayPool<byte>.Shared.Return(tmp);
                    }
                }
                lumaArmed = false;
                lumaReady.Set();
            }

            if (!measuring) return;
            if (!seenFirst)
            {
                seenFirst = true;
                startTicks = Stopwatch.GetTimestamp();
                firstFrame.Set();
            }
            Interlocked.Increment(ref frameCount);
            Interlocked.Add(ref byteCount, dataSize);
        };

        if (!cam.Start())
        {
            Console.WriteLine("[ERR] Camera start failed.");
            return 2;
        }

        Thread.Sleep(TimeSpan.FromSeconds(opts.WarmupSeconds));
        Console.WriteLine("[INFO] Warmup done.");

        if (opts.SweepExposure)
        {
            for (long exp = -11; exp <= -2; exp++)
            {
                bool expOk = cam.SetExposure(exp);
                Thread.Sleep(300);
                Console.WriteLine($"[SWEEP] Exposure={exp} set={(expOk ? "OK" : "FAIL")}");
                RunOneSeries(cam, opts, firstFrame, ref measuring, ref seenFirst, ref startTicks, ref frameCount, ref byteCount, tag: $"EXP {exp}");
            }
        }
        else if (opts.SweepBrightness)
        {
            if (nsub != 1)
            {
                Console.WriteLine("[ERR] Brightness sweep requires NV12 negotiated subtype.");
                cam.Stop();
                return 3;
            }

            long start = opts.SweepBrightnessStart;
            long end = opts.SweepBrightnessEnd;
            long step = start >= end ? -1 : 1;
            Console.WriteLine($"[BRIGHT] Sweep start={start}, end={end}, step={step}. Gain should stay fixed.");
            Console.WriteLine("[BRIGHT] 1us is approximately raw exposure -20.");

            for (long exp = start; step < 0 ? exp >= end : exp <= end; exp += step)
            {
                bool expOk = cam.SetExposure(exp);
                long expAppliedRaw = cam.GetExposure();
                int expApplied = unchecked((int)expAppliedRaw);

                Thread.Sleep(200);

                double sumMean = 0.0;
                double minMean = double.MaxValue;
                double maxMean = double.MinValue;
                int got = 0;

                for (int si = 0; si < Math.Max(1, opts.SweepBrightnessSamples); si++)
                {
                    lumaReady.Reset();
                    lumaArmed = true;
                    if (!lumaReady.Wait(TimeSpan.FromSeconds(2)))
                    {
                        lumaArmed = false;
                        continue;
                    }

                    got++;
                    sumMean += lumaMean;
                    if (lumaMean < minMean) minMean = lumaMean;
                    if (lumaMean > maxMean) maxMean = lumaMean;
                    Thread.Sleep(10);
                }

                if (got == 0)
                {
                    Console.WriteLine($"[BRIGHT] req={exp}, set={(expOk ? "OK" : "FAIL")}, applied={expApplied}, luma=TIMEOUT");
                    continue;
                }

                double avgMean = sumMean / got;
                Console.WriteLine($"[BRIGHT] req={exp}, set={(expOk ? "OK" : "FAIL")}, applied={expApplied}, luma_avg={avgMean:F2}, min={minMean:F2}, max={maxMean:F2}, samples={got}");
            }
        }
        else if (opts.DumpProcAmp)
        {
            if (cam.TryGetCameraControlRange(CameraControlProperty.Exposure, out long cMin, out long cMax, out long cStep, out long cDef, out long cCaps))
            {
                bool setOk = cam.SetCameraControlValue(CameraControlProperty.Exposure, cDef, (cCaps & 0x1) != 0);
                Console.WriteLine($"[CAMCTRL] Exposure: range=[{cMin},{cMax}] step={cStep} def={cDef} caps={cCaps} set_test={setOk}");
            }
            else
            {
                Console.WriteLine("[CAMCTRL] Exposure: N/A");
            }

            var props = new[]
            {
                ProcAmpProperty.Brightness,
                ProcAmpProperty.Contrast,
                ProcAmpProperty.Saturation,
                ProcAmpProperty.Hue,
                ProcAmpProperty.Sharpness,
                ProcAmpProperty.WhiteBalance,
                ProcAmpProperty.BacklightCompensation,
                ProcAmpProperty.Gain
            };

            foreach (var p in props)
            {
                if (cam.TryGetProcAmpRange(p, out long min, out long max, out long step, out long def, out long caps))
                {
                    bool setOk = cam.SetProcAmpValue(p, def, false);
                    Console.WriteLine($"[PROCAMP] {p}: range=[{min},{max}] step={step} def={def} caps={caps} set_def={setOk}");
                }
                else
                {
                    Console.WriteLine($"[PROCAMP] {p}: N/A");
                }
            }
        }
        else
        {
            for (int i = 1; i <= opts.Runs; i++)
            {
                RunOneSeries(cam, opts, firstFrame, ref measuring, ref seenFirst, ref startTicks, ref frameCount, ref byteCount, tag: $"RUN {i}");
            }
        }

        cam.Stop();
        Console.WriteLine("[DONE]");
        return 0;
    }

    private static Options ParseArgs(string[] args)
    {
        var opt = new Options();
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            if ((a == "--runs" || a == "-r") && i + 1 < args.Length && int.TryParse(args[++i], out int runs) && runs > 0)
            {
                opt.Runs = runs;
            }
            else if ((a == "--seconds" || a == "-s") && i + 1 < args.Length && double.TryParse(args[++i], out double sec) && sec > 0)
            {
                opt.MeasureSeconds = sec;
            }
            else if ((a == "--warmup" || a == "-w") && i + 1 < args.Length && double.TryParse(args[++i], out double warmup) && warmup >= 0)
            {
                opt.WarmupSeconds = warmup;
            }
            else if (a == "--mode" && i + 1 < args.Length)
            {
                string mode = args[++i].Trim().ToLowerInvariant();
                opt.Mode = mode == "mjpeg" ? Preferred4KMode.A_MjpegToNv12 : Preferred4KMode.B_Nv12Native;
            }
            else if ((a == "--exposure" || a == "-e") && i + 1 < args.Length && long.TryParse(args[++i], out long exp))
            {
                opt.Exposure = exp;
            }
            else if ((a == "--gain" || a == "-g") && i + 1 < args.Length && int.TryParse(args[++i], out int gain))
            {
                opt.Gain = gain;
            }
            else if (a == "--width" && i + 1 < args.Length && int.TryParse(args[++i], out int width) && width > 0)
            {
                opt.Width = width;
            }
            else if (a == "--height" && i + 1 < args.Length && int.TryParse(args[++i], out int height) && height > 0)
            {
                opt.Height = height;
            }
            else if (a == "--sweep-exposure")
            {
                opt.SweepExposure = true;
            }
            else if (a == "--sweep-brightness")
            {
                opt.SweepBrightness = true;
            }
            else if (a == "--sweep-brightness-start" && i + 1 < args.Length && long.TryParse(args[++i], out long sbs))
            {
                opt.SweepBrightnessStart = sbs;
            }
            else if (a == "--sweep-brightness-end" && i + 1 < args.Length && long.TryParse(args[++i], out long sbe))
            {
                opt.SweepBrightnessEnd = sbe;
            }
            else if (a == "--sweep-brightness-samples" && i + 1 < args.Length && int.TryParse(args[++i], out int sbsamp) && sbsamp > 0)
            {
                opt.SweepBrightnessSamples = sbsamp;
            }
            else if (a == "--dump-procamp")
            {
                opt.DumpProcAmp = true;
            }
        }
        return opt;
    }

    private static void RunOneSeries(
        CameraManager cam,
        Options opts,
        ManualResetEventSlim firstFrame,
        ref bool measuring,
        ref bool seenFirst,
        ref long startTicks,
        ref long frameCount,
        ref long byteCount,
        string tag)
    {
        cam.ResetPerfStats();
        Interlocked.Exchange(ref frameCount, 0);
        Interlocked.Exchange(ref byteCount, 0);
        firstFrame.Reset();
        measuring = true;
        seenFirst = false;

        if (!firstFrame.Wait(TimeSpan.FromSeconds(2)))
        {
            measuring = false;
            Console.WriteLine($"[{tag}] no first frame within timeout.");
            return;
        }

        Thread.Sleep(TimeSpan.FromSeconds(opts.MeasureSeconds));
        long endTicks = Stopwatch.GetTimestamp();
        measuring = false;

        long fc = Interlocked.Read(ref frameCount);
        long bc = Interlocked.Read(ref byteCount);
        double elapsed = (endTicks - startTicks) / (double)Stopwatch.Frequency;
        double fpsByCount = elapsed > 0.0 ? fc / elapsed : 0.0;
        double mbps = elapsed > 0.0 ? (bc / (1024.0 * 1024.0)) / elapsed : 0.0;
        double tsFps = cam.GetTimestampFPS();
        long drop = cam.GetEstimatedDroppedFrames();
        double sdkFps = cam.GetCurrentFPS();

        Console.WriteLine($"[{tag}] frames={fc}, elapsed={elapsed:F3}s, fps={fpsByCount:F2}, ts={tsFps:F2}, sdk={sdkFps:F2}, drop={drop}, payload={mbps:F2} MB/s");
    }
}



