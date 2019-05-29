﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Bot.Builder.Dialogs;
using Microsoft.Bot.Builder.Dialogs.Choices;
using Microsoft.Bot.Builder.Solutions.Responses;
using Microsoft.Bot.Connector;
using Microsoft.Bot.Schema;
using Microsoft.Rest.Serialization;

namespace Microsoft.Bot.Builder.Solutions.Authentication
{
    public class MultiProviderAuthDialog : ComponentDialog
    {
        private string _selectedAuthType = string.Empty;
        private List<OAuthConnection> _authenticationConnections;
        private ResponseManager _responseManager;
        private bool localAuthConfigured = false;

        public MultiProviderAuthDialog(List<OAuthConnection> authenticationConnections)
            : base(nameof(MultiProviderAuthDialog))
        {
            _authenticationConnections = authenticationConnections;
            _responseManager = new ResponseManager(
                new string[] { "en", "de", "es", "fr", "it", "zh" },
                new AuthenticationResponses());

            var firstStep = new WaterfallStep[]
            {
                FirstStepAsync,
            };

            var remoteAuth = new WaterfallStep[]
            {
                SendRemoteEventAsync,
                ReceiveRemoteEventAsync,
            };

            var localAuth = new WaterfallStep[]
            {
                PromptForProviderAsync,
                PromptForAuthAsync,
                HandleTokenResponseAsync,
            };

            AddDialog(new WaterfallDialog(DialogIds.FirstStepPrompt, firstStep));

            // Add remote authentication support
            AddDialog(new WaterfallDialog(DialogIds.RemoteAuthPrompt, remoteAuth));
            AddDialog(new EventPrompt(DialogIds.RemoteAuthEventPrompt, TokenEvents.TokenResponseEventName, TokenResponseValidatorAsync));

            // If authentication connections are provided locally then we enable "local auth" otherwise we only enable remote auth where the calling Bot handles this for us.
            if (_authenticationConnections.Any())
            {
                bool authDialogAdded = false;

                foreach (var connection in _authenticationConnections)
                {
                    // We ignore placeholder connections in config that don't have a Name
                    if (!string.IsNullOrEmpty(connection.Name))
                    {
                        AddDialog(new OAuthPrompt(
                            connection.Name,
                            new OAuthPromptSettings
                            {
                                ConnectionName = connection.Name,
                                Title = "Login",
                                Text = string.Format("Login with {0}", connection.Name),
                            },
                            AuthPromptValidatorAsync));

                        authDialogAdded = true;
                    }
                }

                // Only add Auth supporting local auth dialogs if we found valid authentication connections to use otherwise it will just work in remote mode.
                if (authDialogAdded)
                {
                    AddDialog(new WaterfallDialog(DialogIds.LocalAuthPrompt, localAuth));
                    AddDialog(new ChoicePrompt(DialogIds.ProviderPrompt) { Style = ListStyle.SuggestedAction });

                    localAuthConfigured = true;
                }
            }
        }

        // Validators
        protected Task<bool> TokenResponseValidatorAsync(PromptValidatorContext<Activity> pc, CancellationToken cancellationToken)
        {
            var activity = pc.Recognized.Value;
            if (activity != null && activity.Type == ActivityTypes.Event)
            {
                return Task.FromResult(true);
            }
            else
            {
                return Task.FromResult(false);
            }
        }

        private async Task<DialogTurnResult> FirstStepAsync(WaterfallStepContext stepContext, CancellationToken canellationToken)
        {
            if (stepContext.Context.Adapter is IRemoteUserTokenProvider remoteInvocationAdapter)
            {
                return await stepContext.BeginDialogAsync(DialogIds.RemoteAuthPrompt).ConfigureAwait(false);
            }
            else if (!string.IsNullOrEmpty(stepContext.Context.Activity.ChannelId) && stepContext.Context.Activity.ChannelId == "directlinespeech")
            {
                // Speech channel doesn't support OAuthPrompt./OAuthCards so we rely on tokens being set by the Linked Accounts technique
                // Therefore we don't use OAuthPrompt and instead attempt to directly retrieve the token from the store.
                if (stepContext.Context.Activity.From == null || string.IsNullOrWhiteSpace(stepContext.Context.Activity.From.Id))
                {
                    throw new ArgumentNullException("Missing From or From.Id which is required for token retrieval.");
                }

                var connectorClient = stepContext.Context.TurnState.Get<IConnectorClient>();
                if (connectorClient == null)
                {
                    throw new InvalidOperationException("An IConnectorClient is required in TurnState for this operation.");
                }

                var client = new OAuthClient(new Uri(OAuthClientConfig.OAuthEndpoint), connectorClient.Credentials);

                // Attempt to retrieve the token directly
                var tokenResponse = await client.UserToken.GetTokenWithHttpMessagesAsync(
                    stepContext.Context.Activity.From.Id,
                    _authenticationConnections.First().Name,
                    stepContext.Context.Activity.ChannelId,
                    null,
                    null,
                    canellationToken).ConfigureAwait(false);

                if (tokenResponse != null)
                {
                    return await stepContext.EndDialogAsync(tokenResponse).ConfigureAwait(false);
                }
                else
                {
                    // TODO - Explore a more elegant approach.
                    throw new Exception("Could not retrieve a Token for the user as expected.");
                }
            }
            else
            {
                if (localAuthConfigured)
                {
                    return await stepContext.BeginDialogAsync(DialogIds.LocalAuthPrompt).ConfigureAwait(false);
                }
                else
                {
                    throw new Exception("Local authentication is not configured, please check the authentication connection section in your configuration file.");
                }
            }
        }

        private async Task<DialogTurnResult> SendRemoteEventAsync(WaterfallStepContext stepContext, CancellationToken canellationToken)
        {
            if (stepContext.Context.Adapter is IRemoteUserTokenProvider remoteInvocationAdapter)
            {
                await remoteInvocationAdapter.SendRemoteTokenRequestEventAsync(stepContext.Context, canellationToken).ConfigureAwait(false);

                // Wait for the tokens/response event
                return await stepContext.PromptAsync(DialogIds.RemoteAuthEventPrompt, new PromptOptions()).ConfigureAwait(false);
            }
            else
            {
                throw new Exception("The adapter does not support RemoteTokenRequest.");
            }
        }

        private async Task<DialogTurnResult> ReceiveRemoteEventAsync(WaterfallStepContext stepContext, CancellationToken canellationToken)
        {
            if (stepContext.Context.Activity != null && stepContext.Context.Activity.Value != null)
            {
                var tokenResponse = SafeJsonConvert.DeserializeObject<ProviderTokenResponse>(stepContext.Context.Activity.Value.ToString(), Serialization.Settings);
                return await stepContext.EndDialogAsync(tokenResponse).ConfigureAwait(false);
            }
            else
            {
                throw new Exception("Token Response is invalid.");
            }
        }

        private async Task<DialogTurnResult> PromptForProviderAsync(WaterfallStepContext stepContext, CancellationToken cancellationToken)
        {
            if (_authenticationConnections.Count() == 1)
            {
                var result = _authenticationConnections.ElementAt(0).Name;
                return await stepContext.NextAsync(result).ConfigureAwait(false);
            }
            else
            {
                var adapter = stepContext.Context.Adapter as BotFrameworkAdapter;
                var tokenStatusCollection = await adapter.GetTokenStatusAsync(stepContext.Context, stepContext.Context.Activity.From.Id).ConfigureAwait(false);

                var matchingProviders = tokenStatusCollection.Where(p => (bool)p.HasToken && _authenticationConnections.Any(t => t.Name == p.ConnectionName)).ToList();

                if (matchingProviders.Count() == 1)
                {
                    var authType = matchingProviders[0].ConnectionName;
                    return await stepContext.NextAsync(authType).ConfigureAwait(false);
                }
                else if (matchingProviders.Count() > 1)
                {
                    var choices = new List<Choice>();

                    foreach (var connection in matchingProviders)
                    {
                        choices.Add(new Choice()
                        {
                            Action = new CardAction(ActionTypes.ImBack, connection.ConnectionName, value: connection.ConnectionName),
                            Value = connection.ConnectionName,
                        });
                    }

                    return await stepContext.PromptAsync(DialogIds.ProviderPrompt, new PromptOptions
                    {
                        Prompt = _responseManager.GetResponse(AuthenticationResponses.ConfiguredAuthProvidersPrompt),
                        Choices = choices,
                    }).ConfigureAwait(false);
                }
                else
                {
                    var choices = new List<Choice>();

                    foreach (var connection in _authenticationConnections)
                    {
                        choices.Add(new Choice()
                        {
                            Action = new CardAction(ActionTypes.ImBack, connection.Name, value: connection.Name),
                            Value = connection.Name,
                        });
                    }

                    return await stepContext.PromptAsync(DialogIds.ProviderPrompt, new PromptOptions
                    {
                        Prompt = _responseManager.GetResponse(AuthenticationResponses.AuthProvidersPrompt),
                        Choices = choices,
                    }).ConfigureAwait(false);
                }
            }
        }

        private async Task<DialogTurnResult> PromptForAuthAsync(WaterfallStepContext stepContext, CancellationToken cancellationToken)
        {
            if (stepContext.Result is string)
            {
                _selectedAuthType = stepContext.Result as string;
            }
            else if (stepContext.Result is FoundChoice)
            {
                var choice = stepContext.Result as FoundChoice;
                _selectedAuthType = choice.Value;
            }

            return await stepContext.PromptAsync(_selectedAuthType, new PromptOptions()).ConfigureAwait(false);
        }

        private async Task<DialogTurnResult> HandleTokenResponseAsync(WaterfallStepContext stepContext, CancellationToken cancellationToken)
        {
            if (stepContext.Result is TokenResponse tokenResponse && !string.IsNullOrWhiteSpace(tokenResponse.Token))
            {
                var result = await CreateProviderTokenResponseAsync(stepContext.Context, tokenResponse).ConfigureAwait(false);

                return await stepContext.EndDialogAsync(result).ConfigureAwait(false);
            }
            else
            {
                TelemetryClient.TrackEvent("TokenRetrievalFailure");
                return new DialogTurnResult(DialogTurnStatus.Cancelled);
            }
        }

        private async Task<ProviderTokenResponse> CreateProviderTokenResponseAsync(ITurnContext context, TokenResponse tokenResponse)
        {
            var adapter = context.Adapter as BotFrameworkAdapter;
            var tokens = await adapter.GetTokenStatusAsync(context, context.Activity.From.Id).ConfigureAwait(false);
            var match = Array.Find(tokens, t => t.ConnectionName == tokenResponse.ConnectionName);

            return new ProviderTokenResponse
            {
                AuthenticationProvider = match.ServiceProviderDisplayName.GetAuthenticationProvider(),
                TokenResponse = tokenResponse,
            };
        }

        private Task<bool> AuthPromptValidatorAsync(PromptValidatorContext<TokenResponse> promptContext, CancellationToken cancellationToken)
        {
            var token = promptContext.Recognized.Value;
            if (token != null && !string.IsNullOrWhiteSpace(token.Token))
            {
                return Task.FromResult(true);
            }
            else
            {
                var eventActivity = promptContext.Context.Activity.AsEventActivity();
                if (eventActivity != null && eventActivity.Name == "tokens/response")
                {
                    promptContext.Recognized.Value = eventActivity.Value as TokenResponse;
                    return Task.FromResult(true);
                }
                else
                {
                    TelemetryClient.TrackEvent("AuthPromptValidatorAsyncFailure");
                    return Task.FromResult(false);
                }
            }
        }

        private class DialogIds
        {
            public const string ProviderPrompt = "ProviderPrompt";
            public const string FirstStepPrompt = "FirstStep";
            public const string LocalAuthPrompt = "LocalAuth";
            public const string RemoteAuthPrompt = "RemoteAuth";
            public const string RemoteAuthEventPrompt = "RemoteAuthEvent";
        }
    }
}