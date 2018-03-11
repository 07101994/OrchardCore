using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NCrontab;
using OrchardCore.Environment.Shell;
using OrchardCore.Environment.Shell.Models;
using OrchardCore.Hosting.ShellBuilders;

namespace OrchardCore.BackgroundTasks
{
    public class BackgroundHostedService : BackgroundService
    {
        private static TimeSpan PollingTime = TimeSpan.FromMinutes(1);
        private static TimeSpan MinIdleTime = TimeSpan.FromSeconds(10);
        private readonly ConcurrentDictionary<string, Scheduler> _schedulers = new ConcurrentDictionary<string, Scheduler>();
        private readonly IShellHost _shellHost;

        public BackgroundHostedService(
            IShellHost shellHost,
            ILogger<BackgroundHostedService> logger)
        {
            _shellHost = shellHost;
            Logger = logger;
        }

        public ILogger Logger { get; set; }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            stoppingToken.Register(() => Logger.LogDebug($"BackgroundHostedService is stopping."));

            var startUtc = DateTime.UtcNow;

            while (!stoppingToken.IsCancellationRequested)
            {
                var pollingDelay = Task.Delay(PollingTime, stoppingToken);

                var shells = GetRunningShells(out var tenants);

                CleanSchedulers(tenants);

                Parallel.ForEach(shells, new ParallelOptions { MaxDegreeOfParallelism = 8 }, async shell =>
                {
                    if (shell.Released || stoppingToken.IsCancellationRequested)
                    {
                        return;
                    }

                    IEnumerable<Type> taskTypes;

                    using (var scope = shell.EnterServiceScope())
                    {
                        taskTypes = scope.GetBackgroundTaskTypes();
                    }

                    var tenant = shell.Settings.Name;

                    foreach (var taskType in taskTypes)
                    {
                        var taskName = taskType.FullName;

                        if (shell.Released || stoppingToken.IsCancellationRequested)
                        {
                            break;
                        }

                        using (var scope = shell.EnterServiceScope())
                        {
                            var task = scope.GetBackgroundTaskOfType(taskType);

                            if (task == null)
                            {
                                continue;
                            }

                            if (!_schedulers.TryGetValue(tenant + taskName, out Scheduler scheduler))
                            {
                                _schedulers[tenant + taskName] = scheduler = new Scheduler(tenant, task, startUtc);
                            }

                            if (!scheduler.ShouldRun())
                            {
                                continue;
                            }

                            try
                            {
                                if (Logger.IsEnabled(LogLevel.Information))
                                {
                                    Logger.LogInformation(
                                        "Start processing background task \"{0}\" on tenant \"{1}\".",
                                        tenant, taskName);
                                }

                                await task.DoWorkAsync(scope.ServiceProvider, stoppingToken);

                                if (Logger.IsEnabled(LogLevel.Information))
                                {
                                    Logger.LogInformation(
                                        "Finished processing background task \"{0}\" on tenant \"{1}\".",
                                        tenant, taskName);
                                }
                            }

                            catch (Exception ex)
                            {
                                if (Logger.IsEnabled(LogLevel.Error))
                                {
                                    Logger.LogError(ex,
                                        "Error while processing background task \"{0}\" on tenant \"{1}\".",
                                        tenant, taskName);
                                }
                            }
                        }
                    }
                });

                startUtc = DateTime.UtcNow;
                await Task.Delay(MinIdleTime, stoppingToken);
                await pollingDelay;
            }
        }

        private IEnumerable<ShellContext> GetRunningShells(out IEnumerable<string> tenants)
        {
            var shells = _shellHost.ListShellContexts()?.Where(s => s.Settings?.State == TenantState.Running)
                .OrderBy(s => s.Settings.Name).ToArray() ?? Enumerable.Empty<ShellContext>();

            tenants = shells.Select(s => s.Settings.Name).ToArray();

            return shells;
        }

        private void CleanSchedulers(IEnumerable<string> tenants)
        {
            var keys = _schedulers.Where(kv => !tenants.Contains(kv.Value.Tenant)).Select(kv => kv.Key).ToArray();

            foreach (var key in keys)
            {
                _schedulers.TryRemove(key, out var scheduler);
            }
        }

        private class Scheduler
        {
            private readonly string _schedule;
            private DateTime _startUtc;

            public Scheduler(string tenant, IBackgroundTask task, DateTime startUtc)
            {
                Tenant = tenant;
                var attribute = task.GetType().GetCustomAttribute<BackgroundTaskAttribute>();
                _schedule = attribute?.Schedule ?? "* * * * *";
                _startUtc = startUtc;
            }

            public string Tenant { get; }

            public bool ShouldRun()
            {
                var now = DateTime.UtcNow;

                if (now >= CrontabSchedule.Parse(_schedule).GetNextOccurrence(_startUtc))
                {
                    _startUtc = now;
                    return true;
                }

                return false;
            }
        }
    }

    internal static class ServiceScopeExtensions
    {
        public static IEnumerable<Type> GetBackgroundTaskTypes(this IServiceScope scope)
        {
            return scope.ServiceProvider.GetServices<IBackgroundTask>().Select(t => t.GetType());
        }

        public static IBackgroundTask GetBackgroundTaskOfType(this IServiceScope scope, Type type)
        {
            return scope.ServiceProvider.GetServices<IBackgroundTask>().FirstOrDefault(t => t.GetType() == type);
        }
    }
}