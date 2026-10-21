# Fuzz findings

Minimised inputs that once made the filename parser throw, hang or break a rule of KASI-ENG-003. Each file is a header, a newline and a URL. `FileNameFuzzTests.Every_saved_fuzz_finding_still_passes_the_invariants` replays every file here on every build, so a fixed bug stays fixed. The nightly fuzz run uploads new findings as a workflow artifact; add the minimised file here with the fix.
