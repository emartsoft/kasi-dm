# Host fuzz findings

Minimised inputs that once made the native messaging host (`Kasi.NativeHost`: frame reader, strict parser, error replies) crash, hang or break a rule of KASI-NFR-010. The first byte of a file picks the mode (even: the rest is the payload of one well-formed frame; odd: the rest is raw stdin), see `HostMessageFuzz`. `NativeHostFuzzTests.Every_seed_and_saved_finding_still_passes_the_host_invariants` replays every file here on every build, so a fixed bug stays fixed. The nightly fuzz run uploads new findings as a workflow artifact; add the minimised file here with the fix.
