// CursedForge fork (branch cursedforge/no-alloc): the parity and allocation check.
//
// One program, built twice: Reference/ against the original Concentus 2.2.2
// NuGet package, Fork/ against this fork. run-parity.ps1 builds both, runs
// "parity" on each and compares the two output files byte for byte, then runs
// "alloc" on the fork.
//
// parity <file>  Encodes and decodes fixed synthetic signals (speech-like,
//                silence, noise, a sweep, a clipped tone, and a mix of bursts
//                and gaps) through 8 encoder settings, with a fixed loss
//                pattern (concealment), some FEC decodes and a decoder reset.
//                Writes every packet byte and every decoded sample.
// alloc          The CursedForge voice settings (16 kHz VOIP, 16 kbit/s, VBR,
//                complexity 5, 20 ms). Warms one encoder and one decoder on a
//                varied pass, then measures the bytes this thread allocates
//                in encode, decode and concealment over two more varied
//                passes. Exit code 1 if any byte is allocated.

using System;
using System.IO;
using Concentus.Enums;
using Concentus.Structs;

internal static class ParityProgram
{
    private struct Config
    {
        public int Fs;
        public bool Voip;
        public int Bitrate;
        public int Complexity;
        public bool Vbr;
        public int Ms;
    }

    private static readonly Config[] Configs =
    {
        new Config { Fs = 16000, Voip = true, Bitrate = 16000, Complexity = 5, Vbr = true, Ms = 20 }, // the CursedForge voice settings
        new Config { Fs = 16000, Voip = true, Bitrate = 16000, Complexity = 0, Vbr = true, Ms = 20 },
        new Config { Fs = 16000, Voip = true, Bitrate = 16000, Complexity = 10, Vbr = true, Ms = 20 },
        new Config { Fs = 16000, Voip = true, Bitrate = 12000, Complexity = 5, Vbr = false, Ms = 20 },
        new Config { Fs = 16000, Voip = true, Bitrate = 16000, Complexity = 5, Vbr = true, Ms = 10 },
        new Config { Fs = 16000, Voip = true, Bitrate = 16000, Complexity = 5, Vbr = true, Ms = 40 },
        new Config { Fs = 48000, Voip = false, Bitrate = 64000, Complexity = 5, Vbr = true, Ms = 20 }, // CELT
        new Config { Fs = 48000, Voip = true, Bitrate = 24000, Complexity = 8, Vbr = true, Ms = 20 },  // hybrid and mode switches
    };

    private static uint _seed;

    private static int Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "parity")
        {
            return Parity(args[1]);
        }

        if (args.Length == 1 && args[0] == "alloc")
        {
            return Alloc();
        }

        Console.WriteLine("usage: parity <file> | alloc");
        return 2;
    }

    private static float Rand()
    {
        _seed = _seed * 1664525u + 1013904223u;
        return ((_seed >> 8) / 8388608f) - 1f;
    }

    private static void Fill(int kind, int frameIndex, short[] pcm, int n, int fs)
    {
        for (int i = 0; i < n; i++)
        {
            double t = (frameIndex * n + i) / (double)fs;
            double v;

            switch (kind)
            {
                case 0:
                {
                    // Speech-like: a gliding pitch, 12 harmonics, a syllable envelope.
                    double f0 = 110 + 40 * Math.Sin(2 * Math.PI * 0.7 * t);
                    double env = 0.5 + 0.5 * Math.Sin(2 * Math.PI * 3.1 * t);
                    v = 0;

                    for (int h = 1; h <= 12; h++)
                    {
                        v += Math.Sin(2 * Math.PI * f0 * h * t) / h * (1.0 + Math.Sin(h * 0.9 + t));
                    }

                    v = v * 0.18 * env * env + 0.02 * Rand();
                    break;
                }

                case 1:
                    v = 0;
                    break;
                case 2:
                    v = 0.4 * Rand();
                    break;
                case 3:
                    v = 0.6 * Math.Sin(2 * Math.PI * (100 + 3000 * (t % 2.0)) * t);
                    break;
                case 4:
                    v = 1.5 * Math.Sin(2 * Math.PI * 220 * t);
                    break;
                default:
                {
                    // Bursts of tone, near silence and noise.
                    int seg = (frameIndex / 13) % 3;
                    v = seg == 0 ? 0.3 * Math.Sin(2 * Math.PI * 180 * t) * Math.Sin(2 * Math.PI * 4 * t)
                        : seg == 1 ? 0.0005 * Rand()
                        : 0.2 * Rand();
                    break;
                }
            }

            double s = v * 32767.0;
            pcm[i] = s >= 32767 ? (short)32767 : s <= -32768 ? (short)-32768 : (short)s;
        }
    }

    // Single losses and bursts of two and three.
    private static bool Lost(int f) =>
        (f % 17) == 5 || (f % 41) == 20 || (f % 41) == 21 || (f % 97) == 60 || (f % 97) == 61 || (f % 97) == 62;

    private static OpusEncoder MakeEncoder(Config c, bool voiceSettings)
    {
        var e = new OpusEncoder(c.Fs, 1, c.Voip ? OpusApplication.OPUS_APPLICATION_VOIP : OpusApplication.OPUS_APPLICATION_AUDIO);
        e.Bitrate = c.Bitrate;
        e.UseVBR = c.Vbr;
        e.Complexity = c.Complexity;

        if (voiceSettings)
        {
            e.SignalType = OpusSignal.OPUS_SIGNAL_VOICE;
            e.MaxBandwidth = OpusBandwidth.OPUS_BANDWIDTH_WIDEBAND;
        }

        return e;
    }

    private static int Parity(string outPath)
    {
        const int Frames = 400;
        int exceptions = 0;

        using (var w = new BinaryWriter(File.Create(outPath)))
        {
            for (int ci = 0; ci < Configs.Length; ci++)
            {
                for (int kind = 0; kind <= 5; kind++)
                {
                    Config c = Configs[ci];
                    _seed = (uint)(ci * 100 + kind + 1);
                    int n = c.Fs * c.Ms / 1000;
                    OpusEncoder enc = MakeEncoder(c, ci == 0);
                    var dec = new OpusDecoder(c.Fs, 1);
                    var pcm = new short[n];
                    var outPcm = new short[n * 3];
                    var packet = new byte[1275];

                    for (int f = 0; f < Frames; f++)
                    {
                        Fill(kind, f, pcm, n, c.Fs);
                        int len = enc.Encode(pcm, n, packet, ci == 0 ? 160 : 1275);
                        w.Write(len);
                        w.Write(packet, 0, len);
                        int got;

                        try
                        {
                            if (Lost(f))
                            {
                                got = dec.Decode(ReadOnlySpan<byte>.Empty, outPcm, n, false);
                            }
                            else if (Lost(f + 1) && (f % 3) == 0)
                            {
                                got = dec.Decode(packet.AsSpan(0, len), outPcm, n, true);
                            }
                            else
                            {
                                got = dec.Decode(packet.AsSpan(0, len), outPcm, n, false);
                            }
                        }
                        catch (Exception ex)
                        {
                            // Both builds must fail the same way on the same frame.
                            got = 0;
                            w.Write(-999);
                            w.Write(ex.GetType().Name + ": " + ex.Message);
                            exceptions++;
                        }

                        w.Write(got);

                        for (int i = 0; i < got; i++)
                        {
                            w.Write(outPcm[i]);
                        }

                        if (f == 250)
                        {
                            dec.ResetState();
                        }
                    }
                }
            }

            Console.WriteLine("PARITY_DONE configs=" + Configs.Length + " signals=6 frames=" + Frames + " exceptions=" + exceptions + " bytes=" + w.BaseStream.Position);
        }

        return 0;
    }

    private static int Alloc()
    {
        const int N = 320;
        OpusEncoder enc = MakeEncoder(Configs[0], true);
        var dec = new OpusDecoder(16000, 1);
        var pcm = new short[N];
        var outPcm = new short[N];
        var packet = new byte[160];
        long encodeBytes = 0, decodeBytes = 0, concealBytes = 0;
        int frames = 0, decodes = 0, conceals = 0;

        for (int pass = 0; pass < 3; pass++)
        {
            for (int kind = 0; kind <= 5; kind++)
            {
                _seed = (uint)(kind + 1 + pass * 1000);

                for (int f = 0; f < 400; f++)
                {
                    Fill(kind, f + pass * 977, pcm, N, 16000);

                    if (pass > 0)
                    {
                        // A changing gain, so the measured passes are not a copy of the warm-up.
                        float g = 0.2f + 0.8f * (float)(0.5 + 0.5 * Math.Sin(f * 0.37 + pass));

                        for (int q = 0; q < N; q++)
                        {
                            pcm[q] = (short)(pcm[q] * g);
                        }
                    }

                    long a = GC.GetAllocatedBytesForCurrentThread();
                    int len = enc.Encode(pcm, N, packet, 160);
                    long b = GC.GetAllocatedBytesForCurrentThread();
                    bool lost = Lost(f);

                    if (lost)
                    {
                        dec.Decode(ReadOnlySpan<byte>.Empty, outPcm, N, false);
                    }
                    else
                    {
                        dec.Decode(packet.AsSpan(0, len), outPcm, N, false);
                    }

                    long d = GC.GetAllocatedBytesForCurrentThread();

                    if (pass > 0)
                    {
                        frames++;
                        encodeBytes += b - a;

                        if (lost)
                        {
                            conceals++;
                            concealBytes += d - b;
                        }
                        else
                        {
                            decodes++;
                            decodeBytes += d - b;
                        }
                    }
                }
            }
        }

        Console.WriteLine("ALLOC encode_bytes=" + encodeBytes + " decode_bytes=" + decodeBytes + " conceal_bytes=" + concealBytes
            + " frames=" + frames + " decodes=" + decodes + " conceals=" + conceals
            + " per_frame encode=" + (encodeBytes / frames) + " decode=" + (decodeBytes / Math.Max(1, decodes)) + " conceal=" + (concealBytes / Math.Max(1, conceals)));
        return encodeBytes == 0 && decodeBytes == 0 && concealBytes == 0 ? 0 : 1;
    }
}
