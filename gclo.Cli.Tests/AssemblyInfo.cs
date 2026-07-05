// These tests capture the process-global Console (Out/Error/In) to assert CLI
// output. Running test classes in parallel would let one class's redirect clobber
// another's, so the whole assembly runs sequentially.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
