namespace NodeRecoverySetMaintenance
{
    using System;
    using System.Linq;
    using Newtonsoft.Json;
    using Skyline.DataMiner.Automation;
    using Skyline.DataMiner.Net.NodeRecovery.Requests;

    /// <summary>
    /// Represents a DataMiner Automation script.
    /// </summary>
    public class Script
    {
        private const string PARAM_DMAIDS = "DMAIDs";
        private const string PARAM_MAINTENANCE = "In Maintenance";

        /// <summary>
        /// The script entry point.
        /// </summary>
        /// <param name="engine">Link with SLAutomation process.</param>
        public void Run(IEngine engine)
        {
            try
            {
                RunSafe(engine);
            }
            catch (ScriptAbortException)
            {
                // Catch normal abort exceptions (engine.ExitFail or engine.ExitSuccess)
                throw; // Comment if it should be treated as a normal exit of the script.
            }
            catch (ScriptForceAbortException)
            {
                // Catch forced abort exceptions, caused via external maintenance messages.
                throw;
            }
            catch (ScriptTimeoutException)
            {
                // Catch timeout exceptions for when a script has been running for too long.
                throw;
            }
            catch (InteractiveUserDetachedException)
            {
                // Catch a user detaching from the interactive script by closing the window.
                // Only applicable for interactive scripts, can be removed for non-interactive scripts.
                throw;
            }
            catch (Exception e)
            {
                engine.ExitFail("Run|Something went wrong: " + e);
            }
        }

        private void RunSafe(IEngine engine)
        {
            var dmaIds = GetScriptParamInts(engine, PARAM_DMAIDS);

            if (!dmaIds.Any())
                return;

            var inMaintenance = GetScriptParamBool(engine, PARAM_MAINTENANCE);

            var requests = dmaIds
                .Select(dmaId =>
                    new SetMaintenanceRequest()
                    {
                        NodeId = dmaId,
                        InMaintenance = inMaintenance,
                    })
                .ToArray();

            engine.GetUserConnection().HandleMessages(requests);
        }

        private int[] GetScriptParamInts(IEngine engine, string param)
        {
            var paramRaw = engine.GetScriptParam(param)?.Value;
            if (string.IsNullOrWhiteSpace(paramRaw))
                throw new ArgumentNullException(param);

            try
            {
                // first try as json structure (from low code app)
                // eg "["123"]"
                return JsonConvert
                    .DeserializeObject<string[]>(paramRaw)
                    .Select(int.Parse)
                    .ToArray();
            }
            catch (JsonSerializationException)
            {
                // not valid json, try parse as normal input parameters
                // eg "789"
                return paramRaw
                    .Replace(" ", string.Empty) // remove spaces
                    .Split(',')
                    .Select(int.Parse)
                    .ToArray();
            }
            catch (Exception ex)
            {
                throw new Exception($"Failed to parse {param}: " + ex.Message);
            }
        }

        private bool GetScriptParamBool(IEngine engine, string param)
        {
            var paramRaw = engine.GetScriptParam(param)?.Value;

            if (string.IsNullOrWhiteSpace(paramRaw))
                throw new ArgumentNullException(param);

            if (bool.TryParse(paramRaw, out var result))
                return result;

            throw new Exception($"Failed to parse {param} as bool.");
        }
    }
}
