// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Bot.Connector.Authentication;
using Microsoft.Extensions.Configuration;

namespace Microsoft.BotBuilderSamples.SimpleRootBot31
{
    public class MultipleCredentialProvider : ICredentialProvider
    {
        private readonly Dictionary<string, BotConfig> _bots = new Dictionary<string, BotConfig>();

        public MultipleCredentialProvider(IConfiguration configuration)
        {
            CreateBotConfigs(configuration);
        }

        public Task<bool> IsValidAppIdAsync(string appId)
        {
            if (appId == null)
            {
                throw new ArgumentNullException(nameof(appId));
            }

            return Task.FromResult(_bots.ContainsKey(appId));
        }

        public Task<string> GetAppPasswordAsync(string appId)
        {
            if (appId == null)
            {
                throw new ArgumentNullException(nameof(appId));
            }

            BotConfig bot;
            _bots.TryGetValue(appId, out bot);

            if (bot == null)
            {
                throw new KeyNotFoundException($"Bot with appId: '{appId}' not found.");
            }

            return Task.FromResult(bot.MicrosoftAppPassword);
        }

        public Task<bool> IsAuthenticationDisabledAsync()
        {
            return Task.FromResult(_bots.Count == 0);
        }

        private void CreateBotConfigs(IConfiguration configuration)
        {
            var section = configuration?.GetSection("MultipleParentBots");
            var bots = section?.Get<BotConfig[]>();
            if (bots != null)
            {
                foreach (var bot in bots)
                {
                    _bots.Add(bot.MicrosoftAppId, bot);
                }
            }

            AddDefaultMicrosoftAppConfig(configuration);
        }

        private void AddDefaultMicrosoftAppConfig(IConfiguration configuration)
        {
            var appId = configuration.GetSection(MicrosoftAppCredentials.MicrosoftAppIdKey)?.Value;

            // If "MicrosoftAppId" has been provided in IConfiguration, try add it to _bots. 
            if (appId != null)
            {
                var password = configuration.GetSection(MicrosoftAppCredentials.MicrosoftAppPasswordKey)?.Value;
                _bots.TryAdd(appId, new BotConfig
                {
                    MicrosoftAppId = appId,
                    MicrosoftAppPassword = password
                });
            }
        }

#pragma warning disable SA1402 // File may only contain a single type
        private class BotConfig
#pragma warning restore SA1402 // File may only contain a single type
        {
            public string MicrosoftAppId { get; set; }

            public string MicrosoftAppPassword { get; set; }
        }
    }
}
