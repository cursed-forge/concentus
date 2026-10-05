# CursedForge fork of Concentus

This fork is maintained by CursedForge for its voice chat. The branch `cursedforge/no-alloc` starts at upstream commit `6c2328dc19044601e33a9c11628b8d60e1f3011c`. That commit is the source of the Concentus 2.2.2 NuGet package.

## What the branch changes

The managed port keeps the per-call scratch of the C reference (the VARDECL and ALLOC stack arrays) as new heap arrays. In the 2.2.2 package, one encode allocates about 66 KB and one decode or concealment allocates about 7.7 KB (16 kHz, VOIP, 16 kbit/s, VBR, complexity 5, 20 ms frames). On a standalone headset this is about 6 MB of garbage each second.

On this branch:

- Each `OpusEncoder` and `OpusDecoder` owns a `ScratchArena` (`CSharp/Concentus/Common/CPlusPlus/ScratchArena.cs`). The public `Encode` and `Decode` methods make the arena current for the thread. Every former per-call `new T[n]` rents an array of the same length, cleared to zero, from that arena.
- The per-call objects (`EntropyCoder`, the SILK control and NSQ copies, the boxed values, the CELT band contexts) come from the same arena. A `ScratchReset` method puts each one in the state of a new instance.
- Local array initialisers and method-group delegates on the encode and decode paths are replaced by rents or by cached fields.
- The public API does not change.

After a warm-up, encode, decode and concealment allocate 0 bytes per frame, in the `net8.0` build and in the `netstandard2.0` build.

## How to check the branch

Run the parity and allocation check:

```
powershell -NoProfile -ExecutionPolicy Bypass -File CSharp/NoAllocParity/run-parity.ps1
```

The script builds one program against the Concentus 2.2.2 NuGet package and against this fork. It encodes and decodes the same synthetic signals with 8 encoder settings, a loss pattern, FEC decodes and a decoder reset. Then it compares every packet byte and every decoded sample. It also measures the allocations of the CursedForge voice settings. The pass lines are `PARITY_SAME` and `ALLOC_ZERO`, for each of `net8.0` and `netstandard2.0`.

## Rules for the arena

- Rent only scratch that does not live after the call. State that a codec keeps between calls stays a normal field.
- If the code reads the `Length` of an array, use `Rent`, which gives the exact length. Use `RentAtLeast` only where the code does not read `Length`.
- If a loop calls a function a number of times that changes per frame, put `Mark` and `Release` around the scratch of that function, so the arena does not grow.
