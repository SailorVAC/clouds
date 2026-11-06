using SPS.Simulation;

namespace SPS.Runner;

public static class ConsoleReport
{
    public static void Print(SimulationResult result)
    {
        var e = result.Economy;
        var d = result.Diagnostics;
        Console.WriteLine($"{result.Scenario} | Seed {result.Seed} | Duration {result.Duration.TotalSeconds:F3}s");
        Console.WriteLine($"Profit: {e.Profit:F6} credits | Revenue: {e.Revenue:F6} | API cost: {e.ApiCost:F6} | Worker cost: {e.WorkerCost:F6}");
        Console.WriteLine($"{"Request type",-15}{"Offered",10}{"On time",10}{"Failed",10}{"On-time %",12}{"Earned",12}{"Lost",12}{"Mean ms",12}{"P95 ms",12}");
        foreach (var type in result.RequestTypes)
            Console.WriteLine($"{type.Kind,-15}{type.Requests,10}{type.Successful,10}{type.Failed,10}{100 * type.OnTimeRate,12:F2}{type.EarnedRevenue,12:F2}{type.LostRevenue,12:F2}{Milliseconds(type.MeanLatency),12}{Milliseconds(type.P95Latency),12}");
        Console.WriteLine($"Attempts: {d.Attempts}; retries: {d.Retries}; timeouts: {d.Timeouts}; rejections: {d.Rejections}; policy rejections: {d.PolicyRejections}; late responses: {d.LateResponses}");
        Console.WriteLine($"Cache hits: {d.CacheHits}; coalesced customers: {d.CoalescedCustomers}; fetches: {d.Fetches}; stale responses: {d.StaleResponses}");
        Console.WriteLine($"Job reassignments: {d.JobReassignments}; duplicate executions: {d.DuplicateJobExecutions}; obsolete publications rejected: {d.ObsoletePublications}");
        Console.WriteLine($"Expired requests: {d.ExpiredRequests}; wasted instance occupancy: {d.WastedInstanceTime.TotalSeconds:F3}s; mean queue time: {(d.QueueEntries == 0 ? 0 : d.TotalQueueTime.TotalMilliseconds / d.QueueEntries):F3}ms");
        Console.WriteLine($"API instance-seconds: {d.ApiInstanceSeconds:F3}; worker instance-seconds: {d.WorkerInstanceSeconds:F3}; processed events: {d.ProcessedEvents}");
        Console.WriteLine("Instance lifetimes (billing includes idle, startup, draining, and outages):");
        foreach (var instance in result.Instances)
            Console.WriteLine($"  {instance.Role,-6} #{instance.Id,-4} {instance.AllocatedAt.TotalSeconds,8:F3}s -> {instance.StoppedAt.TotalSeconds,8:F3}s; cost {instance.Cost:F6}");
        if (result.Trace.Count != 0) Console.WriteLine("Selected customer trace:");
        foreach (var item in result.Trace)
            Console.WriteLine($"  {item.Time.TotalSeconds,9:F6}s #{item.Sequence,-7} request={item.RequestId} instance={item.InstanceId} job={item.JobId} {item.Kind} {item.Detail}");
    }
    private static string Milliseconds(TimeSpan? value) => value?.TotalMilliseconds.ToString("F3") ?? "n/a";
}
