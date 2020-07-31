// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using Microsoft.DotNet.XHarness.CLI.CommandArguments;
using Microsoft.DotNet.XHarness.CLI.CommandArguments.Wasm;
using Microsoft.DotNet.XHarness.Common.CLI;
using Microsoft.DotNet.XHarness.Common.Execution;
using Microsoft.DotNet.XHarness.Common.Logging;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server.Features;
using OpenQA.Selenium;
using OpenQA.Selenium.Chrome;
using SLogLevel = OpenQA.Selenium.LogLevel;
using LogLevel = Microsoft.Extensions.Logging.LogLevel;

namespace Microsoft.DotNet.XHarness.CLI.Commands.Wasm
{
    internal class WasmTestCommand : TestCommand
    {
        private const string CommandHelp = "Executes tests on WASM using a selected JavaScript engine or browser";

        private readonly WasmTestCommandArguments _arguments = new WasmTestCommandArguments();

        private StreamWriter? _xmlResultsFileWriter = null;
        private bool _hasWasmStdoutPrefix = false;

        protected override TestCommandArguments TestArguments => _arguments;
        protected override string CommandUsage { get; } = "wasm test [OPTIONS] -- [ENGINE OPTIONS]";
        protected override string CommandDescription { get; } = CommandHelp;

        public WasmTestCommand() : base(CommandHelp, true)
        {
        }

        protected override async Task<ExitCode> InvokeInternal(ILogger logger)
        {
            if (_arguments.Engine == JavaScriptEngine.Chrome)
                return await RunWithChrome (logger);

            return await RunWithJSEngine (logger);
        }

        async Task<ExitCode> RunWithJSEngine (ILogger logger)
        {
            var engineBinary = _arguments.Engine switch
            {
                JavaScriptEngine.V8 => "v8",
                JavaScriptEngine.JavaScriptCore => "jsc",
                JavaScriptEngine.SpiderMonkey => "sm",
                _ => throw new ArgumentException()
            };

            var engineArgs = new List<string>();

            if (_arguments.Engine == JavaScriptEngine.V8)
            {
                // v8 needs this flag to enable WASM support
                engineArgs.Add("--expose_wasm");
            }

            engineArgs.AddRange(_arguments.EngineArgs);
            engineArgs.Add(_arguments.JSFile);

            if (_arguments.Engine == JavaScriptEngine.V8 || _arguments.Engine == JavaScriptEngine.JavaScriptCore)
            {
                // v8/jsc want arguments to the script separated by "--", others don't
                engineArgs.Add("--");
            }

            engineArgs.AddRange(PassThroughArguments);

            var xmlResultsFilePath = Path.Combine(_arguments.OutputDirectory, "testResults.xml");
            File.Delete(xmlResultsFilePath);

            try
            {
                var processManager = ProcessManagerFactory.CreateProcessManager();
                var result = await processManager.ExecuteCommandAsync(
                    engineBinary,
                    engineArgs,
                    log: new CallbackLog(m => logger.LogInformation(m)),
                    stdoutLog: new CallbackLog(m => WasmTestLogCallback(m, xmlResultsFilePath, logger)) { Timestamp = false /* we need the plain XML string so disable timestamp */ },
                    stderrLog: new CallbackLog(m => logger.LogError(m)),
                    _arguments.Timeout);

                return result.Succeeded ? ExitCode.SUCCESS : (result.TimedOut ? ExitCode.TIMED_OUT : ExitCode.GENERAL_FAILURE);
            }
            catch (Win32Exception e) when (e.NativeErrorCode == 2)
            {
                logger.LogCritical($"The engine binary `{engineBinary}` was not found");
                return ExitCode.APP_LAUNCH_FAILURE;
            }
        }

        private async Task<ExitCode> RunWithChrome (ILogger logger)
        {
            var options = new ChromeOptions();
            options.SetLoggingPreference (LogType.Browser, SLogLevel.All);
            options.AddArguments (new List<string>(_arguments.EngineArgs)
            {
                "--incognito",
                "--headless"
            });

            return await RunTestsWithWebDriver (new ChromeDriver(options), logger);
        }

        private async Task<ExitCode> RunTestsWithWebDriver (IWebDriver driver, ILogger logger)
        {
            string xmlResultsFilePath = Path.Combine(_arguments.OutputDirectory, "testResults.xml");
            File.Delete(xmlResultsFilePath);

            CancellationTokenSource webServerCts = new CancellationTokenSource ();
            var webServerAddr = await StartWebServer(line => WasmTestLogCallback(line, xmlResultsFilePath, logger), webServerCts.Token);

            // Messages from selenium prepend the url, and location where the message originated
            // Eg. `foo` becomes `http://localhost:8000/xyz.js 0:12 "foo"
            Regex consoleLogRegex = new Regex(@"^\s*[a-z]*://[^\s]+\s+\d+:\d+\s+""(.*)""\s*$");

            var testUrl = BuildUrl(webServerAddr);
            logger.LogTrace($"Opening in browser: {testUrl}");
            driver.Navigate().GoToUrl(testUrl);

            var cts = new CancellationTokenSource();
            var testsTcs = new TaskCompletionSource<ExitCode> ();
            try {
                var logPumpingTask = Task.Run (async () =>
                {
                    try
                    {
                        while (!cts.Token.IsCancellationRequested)
                        {
                            PumpLogs(testsTcs);
                            await Task.Delay (100, cts.Token).ConfigureAwait (false);
                        }
                    }
                    catch (TaskCanceledException)
                    {
                        // Ignore
                    }
                }, cts.Token);

                var task = await Task.WhenAny(logPumpingTask, testsTcs.Task, Task.Delay(_arguments.Timeout, cts.Token)).ConfigureAwait(false);
                if (task == logPumpingTask && logPumpingTask.IsFaulted)
                {
                    throw logPumpingTask.Exception!;
                }

                // Pump any remaining messages
                PumpLogs(testsTcs);

                if (testsTcs.Task.IsCompleted)
                {
                    cts.Cancel();
                    return testsTcs.Task.Result;
                }

                return ExitCode.TIMED_OUT;
            }
            finally
            {
                if (!cts.IsCancellationRequested)
                {
                    cts.Cancel();
                }

                webServerCts.Cancel();
                driver.Quit();
            }

            void PumpLogs(TaskCompletionSource<ExitCode> tcs)
            {
                foreach (var logEntry in driver.Manage().Logs.GetLog(LogType.Browser))
                {
                    if (logEntry.Level == SLogLevel.Severe)
                    {
                        // throw on driver errors, or other errors that show up
                        // in the console log
                        throw new ArgumentException(logEntry.Message);
                    }

                    var match = consoleLogRegex.Match(Regex.Unescape(logEntry.Message));
                    string msg = match.Success ? match.Groups [1].Value : logEntry.Message;
                    msg += Environment.NewLine;

                    WasmTestLogCallback(msg, xmlResultsFilePath, logger);

                    if (msg.StartsWith ("WASM EXIT "))
                    {
                        if (int.TryParse(msg.Substring("WASM EXIT ".Length).Trim(), out var code))
                        {
                            tcs.SetResult((ExitCode) Enum.ToObject(typeof(ExitCode), code));
                        }
                        else
                        {
                            logger.LogDebug($"Got an unknown exit code in msg: '{msg}'");
                            tcs.SetResult(ExitCode.TESTS_FAILED);
                        }
                    }
                }

                if (driver.CurrentWindowHandle == null) {
                    // this will throw if chrome crashed
                }
            }
        }

        private string BuildUrl(string webServerAddr)
        {
            var uriBuilder = new UriBuilder($"{webServerAddr}/index.html");
            var sb = new StringBuilder();
            foreach (var arg in PassThroughArguments)
            {
                if (sb.Length > 0)
                    sb.Append("&");

                sb.Append($"arg={HttpUtility.UrlEncode(arg)}");
            }

            uriBuilder.Query = sb.ToString();
            return uriBuilder.ToString();
        }

        private static async Task<string> StartWebServer(Action<string> consoleLog, CancellationToken token)
        {
            var host = new WebHostBuilder()
                .UseSetting("UseIISIntegration", false.ToString())
                .UseKestrel()
                .UseContentRoot(Directory.GetCurrentDirectory())
                .UseStartup<WasmTestWebServerStartup>()
                .ConfigureLogging(logging => {
                    logging.AddConsole().AddFilter(null, LogLevel.Debug);
                })
                .UseUrls("http://127.0.0.1:0")
                .Build();

            await host.StartAsync(token);
            return host.ServerFeatures
                    .Get<IServerAddressesFeature>()
                    .Addresses
                    .First();
        }

        private void WasmTestLogCallback(string line, string xmlResultsFilePath, ILogger logger)
        {
            if (_xmlResultsFileWriter == null)
            {
                if (line.Contains("STARTRESULTXML"))
                {
                    _xmlResultsFileWriter = File.CreateText(xmlResultsFilePath);
                    _hasWasmStdoutPrefix = line.StartsWith("WASM: ");
                    return;
                }
                else if (line.Contains("Tests run:"))
                {
                    logger.LogInformation(line);
                }
                else
                {
                    logger.LogDebug(line);
                }
            }
            else
            {
                if (line.Contains("ENDRESULTXML"))
                {
                    _xmlResultsFileWriter.Flush();
                    _xmlResultsFileWriter.Dispose();
                    _xmlResultsFileWriter = null;
                    return;
                }
                _xmlResultsFileWriter.Write(_hasWasmStdoutPrefix ? line.Substring(6) : line);
            }
        }
    }
}
