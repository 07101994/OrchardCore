using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OrchardCore.BackgroundTasks;
using OrchardCore.Environment.Shell;
using OrchardCore.Environment.Shell.Models;
using OrchardCore.Hosting.ShellBuilders;

namespace OrchardCore.Modules
{
    internal class ModularBackgroundService : Internal.BackgroundService, IModularBackgroundService
    {
        private static TimeSpan PollingTime = TimeSpan.FromMinutes(1);
        private static TimeSpan MinIdleTime = TimeSpan.FromSeconds(10);
        private static readonly object _synLock = new object();

        private CancellationTokenSource _updateSource = new CancellationTokenSource();
        private readonly ConcurrentDictionary<string, BackgroundTaskScheduler> _schedulers =
            new ConcurrentDictionary<string, BackgroundTaskScheduler>();

        private readonly IShellHost _shellHost;
        private readonly IHttpContextAccessor _httpContextAccessor;

        public ModularBackgroundService(
            IShellHost shellHost,
            IHttpContextAccessor httpContextAccessor,
            ILogger<ModularBackgroundService> logger)
        {
            _shellHost = shellHost;
            _httpContextAccessor = httpContextAccessor;
            Logger = logger;
        }

        public bool IsRunning { get; private set; }
        public ILogger Logger { get; set; }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            stoppingToken.Register(() =>
            {
                Logger.LogDebug($"{nameof(ModularBackgroundService)} is stopping.");
            });

            var referenceTime = DateTime.UtcNow;

            IEnumerable<ShellContext> shells = null;
            while ((shells?.Count() ?? 0) < 1)
            {
                await Task.Delay(MinIdleTime, stoppingToken);
                shells = GetRunningShells();
            }

            IsRunning = true;

            var updateSource = CancellationTokenSource.CreateLinkedTokenSource(
                _updateSource.Token, stoppingToken);

            while (!stoppingToken.IsCancellationRequested)
            {
                var pollingDelay = Task.Delay(PollingTime, updateSource.Token);

                shells = GetRunningShells();
                var tenants = shells.Select(s => s.Settings?.Name);
                CleanBackgroundTaskSchedulers(tenants);

                await shells.ForEachAsync(async shell =>
                {
                    IEnumerable<Type> taskTypes;
                    var tenant = shell.Settings?.Name;

                    if (shell.Released || stoppingToken.IsCancellationRequested)
                    {
                        return;
                    }

                    using (var scope = shell.EnterServiceScope())
                    {
                        taskTypes = scope.GetBackgroundTaskTypes();
                        CleanTenantBackgroundTaskSchedulers(tenant, taskTypes);
                    }

                    if (taskTypes.Count() > 0)
                    {
                        _httpContextAccessor.HttpContext = shell.GetBackgroundHttpContext();
                    }

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

                            if (!_schedulers.TryGetValue(tenant + taskName, out BackgroundTaskScheduler scheduler))
                            {
                                _schedulers[tenant + taskName] = scheduler = new BackgroundTaskScheduler(tenant, taskName, referenceTime);
                            }

                            try
                            {
                                var settings = await scope.GetBackgroundTaskSettingsAsync(taskType);

                                if (!scheduler.Settings.Schedule.Equals(settings.Schedule))
                                {
                                    scheduler.ReferenceTime = referenceTime;
                                }

                                scheduler.Settings = settings.Clone();

                                if (!scheduler.CanRun())
                                {
                                    continue;
                                }

                                if (Logger.IsEnabled(LogLevel.Information))
                                {
                                    Logger.LogInformation(
                                        "Start processing background task \"{0}\" on tenant \"{1}\".",
                                        tenant, taskName);
                                }

                                scheduler.Run();

                                await task.DoWorkAsync(scope.ServiceProvider, stoppingToken);

                                scheduler.Idle();

                                if (Logger.IsEnabled(LogLevel.Information))
                                {
                                    Logger.LogInformation(
                                        "Finished processing background task \"{0}\" on tenant \"{1}\".",
                                        tenant, taskName);
                                }
                            }

                            catch (Exception ex)
                            {
                                scheduler.Fault(ex);

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

                referenceTime = DateTime.UtcNow;
                var minIdleDelay = Task.Delay(MinIdleTime, updateSource.Token);

                try
                {
                    while (!minIdleDelay.IsCompleted || !pollingDelay.IsCompleted)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(1), updateSource.Token);

                        if (shells.Any(s => s.Released) || shells.Count() != GetRunningShells().Count())
                        {
                            _updateSource.Cancel();
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                }

                if (_updateSource.IsCancellationRequested && !stoppingToken.IsCancellationRequested)
                {
                    lock (_synLock)
                    {
                        _updateSource = new CancellationTokenSource();
                    }

                    updateSource.Dispose();

                    updateSource = CancellationTokenSource.CreateLinkedTokenSource(
                        _updateSource.Token, stoppingToken);
                }
            }

            updateSource.Dispose();
            IsRunning = false;
        }

        public Task UpdateAsync()
        {
            lock (_synLock)
            {
                _updateSource.Cancel();
            }

            return Task.CompletedTask;
        }

        public void Command(string tenant, string taskName, BackgroundTaskScheduler.CommandCode code)
        {
            if (_schedulers.TryRemove(tenant + taskName, out BackgroundTaskScheduler scheduler))
            {
                _schedulers[tenant + taskName] = scheduler.Command(code);
            }
        }

        public Task<BackgroundTaskSettings> GetSettingsAsync(string tenant, string taskName)
        {
            if (_schedulers.TryGetValue(tenant + taskName, out BackgroundTaskScheduler scheduler))
            {
                return Task.FromResult(scheduler.Settings.Clone());
            }

            return Task.FromResult(BackgroundTaskSettings.None);
        }

        public Task<IEnumerable<BackgroundTaskSettings>> GetSettingsAsync(string tenant)
        {
            return Task.FromResult(_schedulers.Where(kv => kv.Value.Tenant == tenant)
                .Select(kv => kv.Value.Settings.Clone()));
        }

        public Task<BackgroundTaskState> GetStateAsync(string tenant, string taskName)
        {
            if (_schedulers.TryGetValue(tenant + taskName, out BackgroundTaskScheduler scheduler))
            {
                return Task.FromResult(scheduler.State.Clone());
            }

            return Task.FromResult(BackgroundTaskState.Undefined);
        }

        public Task<IEnumerable<BackgroundTaskState>> GetStatesAsync(string tenant)
        {
            return Task.FromResult(_schedulers.Where(kv => kv.Value.Tenant == tenant)
                .Select(kv => kv.Value.State.Clone()));
        }

        private IEnumerable<ShellContext> GetRunningShells()
        {
            return _shellHost.ListShellContexts()?.Where(s => s.Settings?.State == TenantState.Running)
                .ToArray() ?? Enumerable.Empty<ShellContext>();
        }

        private void CleanBackgroundTaskSchedulers(IEnumerable<string> tenants)
        {
            var keys = _schedulers.Where(kv => !tenants.Contains(kv.Value.Tenant)).Select(kv => kv.Key).ToArray();

            foreach (var key in keys)
            {
                _schedulers.TryRemove(key, out var scheduler);
            }
        }

        private void CleanTenantBackgroundTaskSchedulers(string tenant, IEnumerable<Type> taskTypes)
        {
            var validKeys = taskTypes.Select(type => tenant + type.FullName);

            var keys = _schedulers.Where(kv => kv.Value.Tenant == tenant).Select(kv => kv.Key).ToArray();

            foreach (var key in keys)
            {
                if (!validKeys.Contains(key))
                {
                    _schedulers.TryRemove(key, out var scheduler);
                }
            }
        }
    }

    internal static class EnumerableExtensions
    {
        public static Task ForEachAsync<T>(this IEnumerable<T> source, Func<T, Task> body)
        {
            var partitionCount = System.Environment.ProcessorCount;

            return Task.WhenAll(
                from partition in Partitioner.Create(source).GetPartitions(partitionCount)
                select Task.Run(async delegate
                {
                    using (partition)
                    {
                        while (partition.MoveNext())
                        {
                            await body(partition.Current);
                        }
                    }
                }));
        }
    }

    internal static class ShellExtensions
    {
        public static HttpContext GetBackgroundHttpContext(this ShellContext shell)
        {
            var httpContext = new DefaultHttpContext();
            httpContext.Request.Host = new HostString(shell.Settings?.RequestUrlHost ?? "localhost");
            httpContext.Request.Path = "/" + shell.Settings?.RequestUrlPrefix ?? "";
            httpContext.Items["IsBackground"] = true;
            return httpContext;
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

        public static async Task<BackgroundTaskSettings> GetBackgroundTaskSettingsAsync(this IServiceScope scope, Type type)
        {
            var providers = scope.ServiceProvider.GetService<IOptions<BackgroundTaskOptions>>()
                .Value.SettingsProviders;

            foreach (var provider in providers.OrderBy(p => p.Order))
            {
                var settings = await provider.GetSettingsAsync(type);

                if (settings != null && settings != BackgroundTaskSettings.None)
                {
                    return settings;
                }
            }

            return new BackgroundTaskSettings() { Name = type.FullName };
        }
    }
}