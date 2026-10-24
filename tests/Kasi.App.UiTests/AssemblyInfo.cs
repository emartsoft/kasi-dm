// One real window at a time: the tests share the desktop (focus, mouse) and UI Automation, so they never run side by side.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
