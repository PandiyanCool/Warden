﻿using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Warden.Configurations;
using Warden.Core;

namespace Warden
{
    /// <summary>
    /// Core interface responsible for executing the watchers, hooks and integrations.
    /// </summary>
    public interface IWarden
    {
        /// <summary>
        /// Customizable name of the Warden.
        /// </summary>
        string Name { get; }

        /// <summary>
        /// Start the Warden. It will be running iterations in a loop (infinite by default but can bo changed) and executing all of the configured hooks.
        /// </summary>
        /// <returns></returns>
        Task StartAsync();

        /// <summary>
        /// Pause the Warden. It will not reset the current iteration number (ordinal) back to 1. Can be resumed by invoking StartAsync().
        /// </summary>
        /// <returns></returns>
        Task PauseAsync();

        /// <summary>
        /// Stop the Warden. It will reset the current iteration number (ordinal) back to 1. Can be resumed by invoking StartAsync().
        /// </summary>
        /// <returns></returns>
        Task StopAsync();
    }

    /// <summary>
    /// Default implementation of the IWarden interface.
    /// </summary>
    public class Warden : IWarden
    {
        private const string UnixComputerNameEnvironmentVariable = "HOSTNAME";
        private const string WindowsComputerNameEnvironmentVariable = "COMPUTERNAME";
        private readonly WardenConfiguration _configuration;
        private long _iterationOrdinal = 1;
        private bool _running = false;

        public string Name { get; }
        public static string DefaultName = GetDefaultName();

        private static string GetDefaultName()
        {
            var environment = string.Empty;
#if DNXCORE50
            environment = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                ? Environment.GetEnvironmentVariable(WindowsComputerNameEnvironmentVariable)
                : Environment.GetEnvironmentVariable(UnixComputerNameEnvironmentVariable);
#else
            environment = Environment.GetEnvironmentVariable(WindowsComputerNameEnvironmentVariable);
#endif

            return $"Warden @{environment}";
        }

        /// <summary>
        /// Initialize a new instance of the Warden using the provided configuration and default name of "Warden @{COMPUTER NAME}".
        /// </summary>
        /// <param name="configuration">Configuration of Warden</param>
        public Warden(WardenConfiguration configuration) : this(DefaultName, configuration)
        {
        }

        /// <summary>
        /// Initialize a new instance of the Warden using the provided configuration.
        /// </summary>
        /// <param name="name">Customizable name of the Warden.</param>
        /// <param name="configuration">Configuration of Warden</param>
        public Warden(string name, WardenConfiguration configuration)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("Warden name can not be empty.", nameof(name));
            if (configuration == null)
                throw new ArgumentNullException(nameof(configuration), "Warden configuration has not been provided.");

            Name = name;
            _configuration = configuration;
        }

        /// <summary>
        /// Start the Warden. 
        /// It will be running iterations in a loop (infinite by default but can bo changed) and executing all of the configured hooks.
        /// </summary>
        /// <returns></returns>
        public async Task StartAsync()
        {
            _running = true;
            _configuration.Hooks.OnStart.Execute();
            await _configuration.Hooks.OnStartAsync.ExecuteAsync();
            var iterationProcessor = _configuration.IterationProcessorProvider();

            while (CanExecuteIteration(_iterationOrdinal))
            {
                try
                {
                    _configuration.Hooks.OnIterationStart.Execute(_iterationOrdinal);
                    await _configuration.Hooks.OnIterationStartAsync.ExecuteAsync(_iterationOrdinal);
                    var iteration = await iterationProcessor.ExecuteAsync(Name, _iterationOrdinal);
                    _configuration.Hooks.OnIterationCompleted.Execute(iteration);
                    await _configuration.Hooks.OnIterationCompletedAsync.ExecuteAsync(iteration);
                    var canExecuteNextIteration = CanExecuteIteration(_iterationOrdinal + 1);
                    if (!canExecuteNextIteration)
                        break;

                    _iterationOrdinal++;
                }
                catch (Exception exception)
                {
                    try
                    {
                        _configuration.Hooks.OnError.Execute(exception);
                        await _configuration.Hooks.OnErrorAsync.ExecuteAsync(exception);
                    }
                    catch (Exception onErrorException)
                    {
                        //Think what to do about it
                    }
                }
                finally
                {
                    await Task.Delay(_configuration.IterationDelay);
                }
            }
        }

        private bool CanExecuteIteration(long ordinal)
        {
            if (!_running)
                return false;
            if (!_configuration.IterationsCount.HasValue)
                return true;
            if (ordinal <= _configuration.IterationsCount)
                return true;

            return false;
        }

        /// <summary>
        /// Pause the Warden. 
        /// It will not reset the current iteration number (ordinal) back to 1. Can be resumed by invoking StartAsync().
        /// </summary>
        /// <returns></returns>
        public async Task PauseAsync()
        {
            _running = false;
            _configuration.Hooks.OnPause.Execute();
            await _configuration.Hooks.OnPauseAsync.ExecuteAsync();
        }

        /// <summary>
        /// Stop the Warden. 
        /// It will reset the current iteration number (ordinal) back to 1. Can be resumed by invoking StartAsync().
        /// </summary>
        /// <returns></returns>
        public async Task StopAsync()
        {
            _running = false;
            _iterationOrdinal = 1;
            _configuration.Hooks.OnStop.Execute();
            await _configuration.Hooks.OnStopAsync.ExecuteAsync();
        }

        /// <summary>
        /// Factory method for creating a new Warden instance with provided configuration and default name of "Warden @{COMPUTER NAME}".
        /// </summary>
        /// <param name="configuration">Configuration of Warden.</param>
        /// <returns>Instance of IWarden.</returns>
        public static IWarden Create(WardenConfiguration configuration) => Create(DefaultName, configuration);

        /// <summary>
        /// Factory method for creating a new Warden instance with provided configuration.
        /// </summary>
        /// <param name="name">Name of the Warden.</param>
        /// <param name="configuration">Configuration of Warden.</param>
        /// <returns>Instance of IWarden.</returns>
        public static IWarden Create(string name, WardenConfiguration configuration) => new Warden(name, configuration);

        /// <summary>
        /// Factory method for creating a new Warden instance with default name of "Warden @{COMPUTER NAME}",
        /// for which the configuration can be provided via the lambda expression.
        /// </summary>
        /// <param name="configurator">Lambda expression to build the configuration of Warden.</param>
        /// <returns>Instance of IWarden.</returns>
        public static IWarden Create(Action<WardenConfiguration.Builder> configurator) => Create(DefaultName, configurator);

        /// <summary>
        /// Factory method for creating a new Warden instance, for which the configuration can be provided via the lambda expression.
        /// </summary>
        /// <param name="name">Name of the Warden.</param>
        /// <param name="configurator">Lambda expression to build the configuration of Warden.</param>
        /// <returns>Instance of IWarden.</returns>
        public static IWarden Create(string name, Action<WardenConfiguration.Builder> configurator)
        {
            var config = new WardenConfiguration.Builder();
            configurator?.Invoke(config);

            return Create(name, config.Build());
        }
    }
}