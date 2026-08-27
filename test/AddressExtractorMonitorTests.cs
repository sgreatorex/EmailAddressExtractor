using System.Text;

using HaveIBeenPwned.AddressExtractor.Objects;
using HaveIBeenPwned.AddressExtractor.Objects.Performance;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HaveIBeenPwned.AddressExtractor.Tests;

/// <summary>
/// Covers the read/parse pipeline in <see cref="AddressExtractorMonitor"/>: lines are
/// buffered into batches before being handed to the parsing tasks, so the batch
/// boundaries, the per-file counters and the written output all need guarding.
/// </summary>
[TestClass]
public class AddressExtractorMonitorTests
{
    private sealed record MonitorResult(IList<string> Addresses, string Report, byte[] RawOutput);

    /// <summary>
    /// Runs the monitor over <paramref name="files"/> exactly as the program does and
    /// returns what it wrote, so the assertions describe observable behaviour rather
    /// than the monitor's internals.
    /// </summary>
    private static async Task<MonitorResult> RunMonitorAsync(string[] files, int? batchSize = null)
    {
        var output = Path.Combine(Path.GetTempPath(), $"{Path.GetRandomFileName()}.out.txt");

        var args = new List<string>(files) { "-y", "-q", "-o", output };
        if (batchSize is { } size)
        {
            args.Add("--batch-size");
            args.Add(size.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        var config = CommandLineProcessor.Parse(args, out _);
        var runtime = new Runtime(config);

        try
        {
            await using (var monitor = new AddressExtractorMonitor(runtime, IPerformanceStack.DEFAULT))
            {
                var fileCount = 0L;
                foreach (var file in files)
                {
                    await monitor.RunAsync(++fileCount, new FileInfo(file)).ConfigureAwait(false);
                }

                await monitor.AwaitCompletionAsync().ConfigureAwait(false);
                await monitor.SaveAsync().ConfigureAwait(false);
            }

            return new MonitorResult(
                await File.ReadAllLinesAsync(output).ConfigureAwait(false),
                await File.ReadAllTextAsync(config.ReportFilePath).ConfigureAwait(false),
                await File.ReadAllBytesAsync(output).ConfigureAwait(false)
            );
        }
        finally
        {
            File.Delete(output);
            File.Delete(config.ReportFilePath);
        }
    }

    /// <summary>Writes <paramref name="content"/> to a uniquely named .txt file</summary>
    private static string WriteTempFile(byte[] content)
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Path.GetRandomFileName()}.txt");
        File.WriteAllBytes(path, content);
        return path;
    }

    private static string WriteAddressFile(int count, int firstIndex = 0)
    {
        var builder = new StringBuilder();
        for (var i = 0; i < count; i++)
        {
            builder.Append("user").Append(firstIndex + i).Append("@example.com\n");
        }

        return WriteTempFile(Encoding.UTF8.GetBytes(builder.ToString()));
    }

    /// <summary>
    /// A batch is only published once it is full, with a flush for the remainder, so an
    /// input that is an exact multiple of the batch size must not lose the final batch
    /// and one that is not must not lose the tail.
    /// </summary>
    [TestMethod]
    public async Task AddressesSurviveBatchBoundariesAsync()
    {
        var batch = new Config().BatchSize;
        int[] sizes = [1, batch - 1, batch, batch + 1, (batch * 2) - 1, batch * 2, (batch * 2) + 1];

        foreach (var size in sizes)
        {
            var file = WriteAddressFile(size);
            try
            {
                var result = await RunMonitorAsync([file]).ConfigureAwait(false);

                Assert.AreEqual(size, result.Addresses.Count, $"All {size} addresses should be extracted when the input spans {size / (double)batch:n2} batches");
                CollectionAssert.AllItemsAreUnique((System.Collections.ICollection)result.Addresses, "Batching should not duplicate addresses");
            }
            finally
            {
                File.Delete(file);
            }
        }
    }

    /// <summary>
    /// Batches from consecutive files can be in flight at the same time, so the per-file
    /// counters have to stay attached to the file the lines came from.
    /// </summary>
    [TestMethod]
    public async Task PerFileCountsAreReportedIndependentlyAsync()
    {
        // Overlapping ranges: every address in the second file also appears in the first.
        var first = WriteAddressFile(count: 1500, firstIndex: 0);
        var second = WriteAddressFile(count: 500, firstIndex: 0);

        try
        {
            var result = await RunMonitorAsync([first, second]).ConfigureAwait(false);

            Assert.AreEqual(1500, result.Addresses.Count, "The union of both files is 1,500 distinct addresses");

            StringAssert.Contains(result.Report, $"{first}: 1,500", "The first file should report every address it contained");
            StringAssert.Contains(result.Report, $"{second}: 500", "The second file should report its own addresses even though the first file saw them already");
        }
        finally
        {
            File.Delete(first);
            File.Delete(second);
        }
    }

    /// <summary>
    /// Batching is a throughput concern and must not be observable in the results, so the
    /// same input has to yield the same addresses whatever size the batches are. A batch
    /// size of 1 is the line at a time behaviour the batching replaced.
    /// </summary>
    [TestMethod]
    public async Task ResultsAreIndependentOfBatchSizeAsync()
    {
        var file = WriteAddressFile(count: 2500);
        try
        {
            var reference = await RunMonitorAsync([file], batchSize: 1).ConfigureAwait(false);
            Assert.AreEqual(2500, reference.Addresses.Count, "One line at a time should find every address");

            // 2500 matches the input exactly and 4096 exceeds it, so both the exact
            // multiple and the never-filled cases are covered.
            foreach (var batchSize in new[] { 2, 7, 64, 2499, 2500, 4096 })
            {
                var result = await RunMonitorAsync([file], batchSize).ConfigureAwait(false);

                CollectionAssert.AreEqual(
                    (System.Collections.ICollection)reference.Addresses,
                    (System.Collections.ICollection)result.Addresses,
                    $"A batch size of {batchSize} should yield the same addresses as one line at a time"
                );
            }
        }
        finally
        {
            File.Delete(file);
        }
    }
}
