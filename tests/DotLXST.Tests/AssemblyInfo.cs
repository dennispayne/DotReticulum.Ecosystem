using Xunit;

// Codec2's excitation generator is process-global, including in the reference.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
