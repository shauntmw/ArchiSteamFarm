﻿/*
    _                _      _  ____   _                           _____
   / \    _ __  ___ | |__  (_)/ ___| | |_  ___   __ _  _ __ ___  |  ___|__ _  _ __  _ __ ___
  / _ \  | '__|/ __|| '_ \ | |\___ \ | __|/ _ \ / _` || '_ ` _ \ | |_  / _` || '__|| '_ ` _ \
 / ___ \ | |  | (__ | | | || | ___) || |_|  __/| (_| || | | | | ||  _|| (_| || |   | | | | | |
/_/   \_\|_|   \___||_| |_||_||____/  \__|\___| \__,_||_| |_| |_||_|   \__,_||_|   |_| |_| |_|

 Copyright 2015-2016 Łukasz "JustArchi" Domeradzki
 Contact: JustArchi@JustArchi.net

 Licensed under the Apache License, Version 2.0 (the "License");
 you may not use this file except in compliance with the License.
 You may obtain a copy of the License at

 http://www.apache.org/licenses/LICENSE-2.0
					
 Unless required by applicable law or agreed to in writing, software
 distributed under the License is distributed on an "AS IS" BASIS,
 WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 See the License for the specific language governing permissions and
 limitations under the License.

*/

using Newtonsoft.Json;
using SteamAuth;
using SteamKit2;
using SteamKit2.Internal;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using System.Text;

namespace ArchiSteamFarm {
	internal sealed class Bot {
		private const ulong ArchiSCFarmGroup = 103582791440160998;
		private const ushort CallbackSleep = 500; // In miliseconds

		internal static readonly ConcurrentDictionary<string, Bot> Bots = new ConcurrentDictionary<string, Bot>();
		internal static readonly HashSet<uint> GlobalBlacklist = new HashSet<uint> { 303700, 335590, 368020, 425280 };

		private static readonly uint LoginID = MsgClientLogon.ObfuscationMask; // This must be the same for all ASF bots and all ASF processes

		private readonly string ConfigFile, LoginKeyFile, MobileAuthenticatorFile, SentryFile;
		private readonly Timer SendItemsTimer;

		internal readonly string BotName;
		internal readonly ArchiHandler ArchiHandler;
		internal readonly ArchiWebHandler ArchiWebHandler;
		internal readonly SteamClient SteamClient;

		private readonly CallbackManager CallbackManager;
		private readonly CardsFarmer CardsFarmer;
		private readonly SteamApps SteamApps;
		private readonly SteamFriends SteamFriends;
		private readonly SteamUser SteamUser;
		private readonly Trading Trading;

		internal bool KeepRunning { get; private set; } = false;
		internal SteamGuardAccount SteamGuardAccount { get; private set; }

		// Config variables
		internal bool Enabled { get; private set; } = false;
		internal string SteamLogin { get; private set; } = "null";
		internal string SteamPassword { get; private set; } = "null";
		internal string SteamNickname { get; private set; } = "null";
		internal string SteamApiKey { get; private set; } = "null";
		internal string SteamParentalPIN { get; private set; } = "0";
		internal ulong SteamMasterID { get; private set; } = 0;
		internal ulong SteamMasterClanID { get; private set; } = 0;
		internal bool StartOnLaunch { get; private set; } = true;
		internal bool CardDropsRestricted { get; private set; } = false;
		internal bool FarmOffline { get; private set; } = false;
		internal bool HandleOfflineMessages { get; private set; } = false;
		internal bool ForwardKeysToOtherBots { get; private set; } = false;
		internal bool DistributeKeys { get; private set; } = false;
		internal bool UseAsfAsMobileAuthenticator { get; private set; } = false;
		internal bool ShutdownOnFarmingFinished { get; private set; } = false;
		internal bool SendOnFarmingFinished { get; private set; } = false;
		internal string SteamTradeToken { get; private set; } = "null";
		internal byte SendTradePeriod { get; private set; } = 0;
		internal HashSet<uint> Blacklist { get; } = new HashSet<uint>();
		internal HashSet<uint> GamesPlayedWhileIdle { get; } = new HashSet<uint>() { 0 };
		internal bool Statistics { get; private set; } = true;

		private bool InvalidPassword = false;
		private bool LoggedInElsewhere = false;
		private string AuthCode, LoginKey, TwoFactorAuth;

		internal static string GetAnyBotName() {
			foreach (string botName in Bots.Keys) {
				return botName;
			}

			return null;
		}

		internal static async Task RefreshCMs() {
			bool initialized = false;
			for (byte i = 0; i < 3 && !initialized; i++) {
				try {
					Logging.LogGenericInfo("Refreshing list of CMs...");
					await SteamDirectory.Initialize().ConfigureAwait(false);
					initialized = true;
				} catch (Exception e) {
					Logging.LogGenericException(e);
					await Utilities.SleepAsync(1000).ConfigureAwait(false);
				}
			}

			if (initialized) {
				Logging.LogGenericInfo("Success!");
			} else {
				Logging.LogGenericWarning("Failed to initialize list of CMs after 3 tries, ASF will use built-in SK2 list, it may take a while to connect");
			}
		}

		private static bool IsValidCdKey(string key) {
			if (string.IsNullOrEmpty(key)) {
				return false;
			}

			// Steam keys are offered in many formats: https://support.steampowered.com/kb_article.php?ref=7480-WUSF-3601
			// It's pointless to implement them all, so we'll just do a simple check if key is supposed to be valid
			// Every valid key, apart from Prey one has at least two dashes
			return Utilities.GetCharCountInString(key, '-') >= 2;
		}

		internal Bot(string botName) {
			if (string.IsNullOrEmpty(botName)) {
				return;
			}

			BotName = botName;

			string botPath = Path.Combine(Program.ConfigDirectory, botName);
			ConfigFile = botPath + ".xml";
			LoginKeyFile = botPath + ".key";
			MobileAuthenticatorFile = botPath + ".auth";
			SentryFile = botPath + ".bin";

			if (!ReadConfig()) {
				return;
			}

			if (!Enabled) {
				return;
			}

			bool alreadyExists;
			lock (Bots) {
				alreadyExists = Bots.ContainsKey(botName);
				if (!alreadyExists) {
					Bots[botName] = this;
				}
			}

			if (alreadyExists) {
				return;
			}

			// Initialize
			SteamClient = new SteamClient();

			ArchiHandler = new ArchiHandler();
			SteamClient.AddHandler(ArchiHandler);

			CallbackManager = new CallbackManager(SteamClient);
			CallbackManager.Subscribe<SteamClient.ConnectedCallback>(OnConnected);
			CallbackManager.Subscribe<SteamClient.DisconnectedCallback>(OnDisconnected);

			SteamApps = SteamClient.GetHandler<SteamApps>();
			CallbackManager.Subscribe<SteamApps.FreeLicenseCallback>(OnFreeLicense);

			SteamFriends = SteamClient.GetHandler<SteamFriends>();
			CallbackManager.Subscribe<SteamFriends.ChatInviteCallback>(OnChatInvite);
			CallbackManager.Subscribe<SteamFriends.ChatMsgCallback>(OnChatMsg);
			CallbackManager.Subscribe<SteamFriends.FriendsListCallback>(OnFriendsList);
			CallbackManager.Subscribe<SteamFriends.FriendMsgCallback>(OnFriendMsg);
			CallbackManager.Subscribe<SteamFriends.FriendMsgHistoryCallback>(OnFriendMsgHistory);

			if (UseAsfAsMobileAuthenticator && File.Exists(MobileAuthenticatorFile)) {
				try {
					SteamGuardAccount = JsonConvert.DeserializeObject<SteamGuardAccount>(File.ReadAllText(MobileAuthenticatorFile));
				} catch (Exception e) {
					Logging.LogGenericException(e, botName);
				}
			}

			SteamUser = SteamClient.GetHandler<SteamUser>();
			CallbackManager.Subscribe<SteamUser.AccountInfoCallback>(OnAccountInfo);
			CallbackManager.Subscribe<SteamUser.LoggedOffCallback>(OnLoggedOff);
			CallbackManager.Subscribe<SteamUser.LoggedOnCallback>(OnLoggedOn);
			CallbackManager.Subscribe<SteamUser.LoginKeyCallback>(OnLoginKey);
			CallbackManager.Subscribe<SteamUser.UpdateMachineAuthCallback>(OnMachineAuth);

			CallbackManager.Subscribe<ArchiHandler.NotificationsCallback>(OnNotifications);
			CallbackManager.Subscribe<ArchiHandler.OfflineMessageCallback>(OnOfflineMessage);
			CallbackManager.Subscribe<ArchiHandler.PurchaseResponseCallback>(OnPurchaseResponse);

			ArchiWebHandler = new ArchiWebHandler(this, SteamApiKey);
			CardsFarmer = new CardsFarmer(this);
			Trading = new Trading(this);

			if (SendTradePeriod > 0 && SendItemsTimer == null) {
				SendItemsTimer = new Timer(
					async e => await ResponseSendTrade().ConfigureAwait(false),
					null,
					TimeSpan.FromHours(SendTradePeriod), // Delay
					TimeSpan.FromHours(SendTradePeriod) // Period
				);
			}

			if (!StartOnLaunch) {
				return;
			}

			// Start
			Start().Wait();
		}

		internal async Task AcceptAllConfirmations() {
			if (SteamGuardAccount == null) {
				return;
			}

			await SteamGuardAccount.RefreshSessionAsync().ConfigureAwait(false);

			try {
				foreach (Confirmation confirmation in await SteamGuardAccount.FetchConfirmationsAsync().ConfigureAwait(false)) {
					if (SteamGuardAccount.AcceptConfirmation(confirmation)) {
						Logging.LogGenericInfo("Accepting confirmation: Success!", BotName);
					} else {
						Logging.LogGenericWarning("Accepting confirmation: Failed!", BotName);
					}
				}
			} catch (SteamGuardAccount.WGTokenInvalidException) {
				Logging.LogGenericWarning("Accepting confirmation: Failed!", BotName);
				Logging.LogGenericWarning("Confirmation could not be accepted because of invalid token exception", BotName);
				Logging.LogGenericWarning("If issue persists, consider removing and readding ASF 2FA", BotName);
			}
		}

		internal void ResetGamesPlayed() {
			if (GamesPlayedWhileIdle.Contains(0)) {
				ArchiHandler.PlayGames(0);
			} else {
				ArchiHandler.PlayGames(GamesPlayedWhileIdle);
			}
		}

		internal async Task Restart() {
			Stop();
			await Utilities.SleepAsync(500).ConfigureAwait(false);
			await Start().ConfigureAwait(false);
		}

		internal async Task OnFarmingFinished(bool farmedSomething) {
			if (farmedSomething && SendOnFarmingFinished) {
				await ResponseSendTrade().ConfigureAwait(false);
			}
			if (ShutdownOnFarmingFinished) {
				Shutdown();
			}
		}

		internal async Task<string> HandleMessage(string message) {
			if (string.IsNullOrEmpty(message)) {
				return null;
			}

			if (!message.StartsWith("!")) {
				return await ResponseRedeem(BotName, message, true).ConfigureAwait(false);
			}

			if (!message.Contains(" ")) {
				switch (message) {
					case "!2fa":
						return Response2FA();
					case "!2faoff":
						return Response2FAOff();
					case "!exit":
						Program.Exit();
						return null;
					case "!rejoinchat":
						return ResponseRejoinChat();
					case "!restart":
						Program.Restart();
						return "Done";
					case "!status":
						return ResponseStatus();
					case "!statusall":
						return ResponseStatusAll();
					case "!stop":
						return ResponseStop();
					case "!loot":
						return await ResponseSendTrade().ConfigureAwait(false);
					default:
						return "Unrecognized command: " + message;
				}
			} else {
				string[] args = message.Split(' ');
				switch (args[0]) {
					case "!2fa":
						return Response2FA(args[1]);
					case "!2faoff":
						return Response2FAOff(args[1]);
					case "!addlicense":
						if (args.Length > 2) {
							return await ResponseAddLicense(args[1], args[2]).ConfigureAwait(false);
						} else {
							return await ResponseAddLicense(BotName, args[1]).ConfigureAwait(false);
						}
					case "!play":
						if (args.Length > 2) {
							return await ResponsePlay(args[1], args[2]).ConfigureAwait(false);
						} else {
							return await ResponsePlay(BotName, args[1]).ConfigureAwait(false);
						}
					case "!redeem":
						if (args.Length > 2) {
							return await ResponseRedeem(args[1], args[2], false).ConfigureAwait(false);
						} else {
							return await ResponseRedeem(BotName, args[1], false).ConfigureAwait(false);
						}
					case "!start":
						return await ResponseStart(args[1]).ConfigureAwait(false);
					case "!stop":
						return ResponseStop(args[1]);
					case "!status":
						return ResponseStatus(args[1]);
					case "!loot":
						return await ResponseSendTrade(args[1]).ConfigureAwait(false);
					default:
						return "Unrecognized command: " + args[0];
				}
			}
		}

		private async Task Start() {
			if (SteamClient.IsConnected) {
				return;
			}

			if (!KeepRunning) {
				KeepRunning = true;
				var handleCallbacks = Task.Run(() => HandleCallbacks());
			}

			Logging.LogGenericInfo("Starting...", BotName);

			// 2FA tokens are expiring soon, use limiter only when we don't have any pending
			if (TwoFactorAuth == null) {
				await Program.LimitSteamRequestsAsync().ConfigureAwait(false);
			}

			SteamClient.Connect();
		}

		private void Stop() {
			if (!SteamClient.IsConnected) {
				return;
			}

			Logging.LogGenericInfo("Stopping...", BotName);

			SteamClient.Disconnect();
		}

		private void Shutdown() {
			KeepRunning = false;
			Stop();
			Program.OnBotShutdown();
		}

		private string ResponseStatus() {
			if (CardsFarmer.CurrentGamesFarming.Count > 0) {
				return "Bot " + BotName + " is currently farming appIDs: " + string.Join(", ", CardsFarmer.CurrentGamesFarming) + " and has a total of " + CardsFarmer.GamesToFarm.Count + " games left to farm.";
			} else {
				return "Bot " + BotName + " is currently not farming anything.";
			}
		}

		private static string ResponseStatus(string botName) {
			if (string.IsNullOrEmpty(botName)) {
				return null;
			}

			Bot bot;
			if (!Bots.TryGetValue(botName, out bot)) {
				return "Couldn't find any bot named " + botName + "!";
			}

			return bot.ResponseStatus();
		}

		private static string ResponseStatusAll() {
			StringBuilder result = new StringBuilder(Environment.NewLine);

			int totalBotsCount = Bots.Count;
			int runningBotsCount = 0;

			foreach (Bot bot in Bots.Values) {
				result.Append(bot.ResponseStatus() + Environment.NewLine);
				if (bot.KeepRunning) {
					runningBotsCount++;
				}
			}

			result.Append("There are " + totalBotsCount + " bots initialized and " + runningBotsCount + " of them are currently running.");
			return result.ToString();
		}

		private async Task<string> ResponseSendTrade() {
			if (SteamMasterID == 0) {
				return "Trade couldn't be send because SteamMasterID is not defined!";
			}

			string token = null;
			if (!string.IsNullOrEmpty(SteamTradeToken) && !SteamTradeToken.Equals("null")) {
				token = SteamTradeToken;
			}

			await Trading.LimitInventoryRequestsAsync().ConfigureAwait(false);
			List<SteamItem> inventory = await ArchiWebHandler.GetMyTradableInventory().ConfigureAwait(false);

			if (inventory == null || inventory.Count == 0) {
				return "Nothing to send, inventory seems empty!";
			}

			if (await ArchiWebHandler.SendTradeOffer(inventory, SteamMasterID, token).ConfigureAwait(false)) {
				await AcceptAllConfirmations().ConfigureAwait(false);
				return "Trade offer sent successfully!";
			} else {
				return "Trade offer failed due to error!";
			}
		}

		private static async Task<string> ResponseSendTrade(string botName) {
			if (string.IsNullOrEmpty(botName)) {
				return null;
			}

			Bot bot;
			if (!Bots.TryGetValue(botName, out bot)) {
				return "Couldn't find any bot named " + botName + "!";
			}

			return await bot.ResponseSendTrade().ConfigureAwait(false);
		}

		private string Response2FA() {
			if (SteamGuardAccount == null) {
				return "That bot doesn't have ASF 2FA enabled!";
			}

			long timeLeft = 30 - TimeAligner.GetSteamTime() % 30;
			return "2FA Token: " + SteamGuardAccount.GenerateSteamGuardCode() + " (expires in " + timeLeft + " seconds)";
		}

		private static string Response2FA(string botName) {
			if (string.IsNullOrEmpty(botName)) {
				return null;
			}

			Bot bot;
			if (!Bots.TryGetValue(botName, out bot)) {
				return "Couldn't find any bot named " + botName + "!";
			}

			return bot.Response2FA();
		}

		private string Response2FAOff() {
			if (SteamGuardAccount == null) {
				return "That bot doesn't have ASF 2FA enabled!";
			}

			if (DelinkMobileAuthenticator()) {
				return "Done! Bot is no longer using ASF 2FA";
			} else {
				return "Something went wrong during delinking mobile authenticator!";
			}
		}

		private static string Response2FAOff(string botName) {
			if (string.IsNullOrEmpty(botName)) {
				return null;
			}

			Bot bot;
			if (!Bots.TryGetValue(botName, out bot)) {
				return "Couldn't find any bot named " + botName + "!";
			}

			return bot.Response2FAOff();
		}

		private async Task<string> ResponseRedeem(string message, bool validate) {
			if (string.IsNullOrEmpty(message)) {
				return null;
			}

			StringBuilder response = new StringBuilder();
			using (StringReader reader = new StringReader(message)) {
				string key = reader.ReadLine();
				IEnumerator<Bot> iterator = Bots.Values.GetEnumerator();
				Bot currentBot = this;
				while (key != null) {
					if (currentBot == null) {
						break;
					}

					if (validate && !IsValidCdKey(key)) {
						key = reader.ReadLine();
						continue;
					}

					ArchiHandler.PurchaseResponseCallback result;
					try {
						result = await currentBot.ArchiHandler.RedeemKey(key);
					} catch (Exception e) {
						Logging.LogGenericException(e, currentBot.BotName);
						break;
					}

					if (result == null) {
						break;
					}

					var purchaseResult = result.PurchaseResult;
					var items = result.Items;

					switch (purchaseResult) {
						case ArchiHandler.PurchaseResponseCallback.EPurchaseResult.AlreadyOwned:
						case ArchiHandler.PurchaseResponseCallback.EPurchaseResult.BaseGameRequired:
						case ArchiHandler.PurchaseResponseCallback.EPurchaseResult.OnCooldown:
						case ArchiHandler.PurchaseResponseCallback.EPurchaseResult.RegionLocked:
							response.Append(Environment.NewLine + "<" + currentBot.BotName + "> Key: " + key + " | Status: " + purchaseResult + " | Items: " + string.Join("", items));
							if (DistributeKeys) {
								do {
									if (iterator.MoveNext()) {
										currentBot = iterator.Current;
									} else {
										currentBot = null;
									}
								} while (currentBot == this);

								if (!ForwardKeysToOtherBots) {
									key = reader.ReadLine();
								}
								break;
							}

							if (!ForwardKeysToOtherBots) {
								key = reader.ReadLine();
								break;
							}

							bool alreadyHandled = false;
							foreach (Bot bot in Bots.Values) {
								if (alreadyHandled) {
									break;
								}

								if (bot == this) {
									continue;
								}

								ArchiHandler.PurchaseResponseCallback otherResult;
								try {
									otherResult = await bot.ArchiHandler.RedeemKey(key);
								} catch (Exception e) {
									Logging.LogGenericException(e, bot.BotName);
									break; // We're done with this key
								}

								if (otherResult == null) {
									break; // We're done with this key
								}

								var otherPurchaseResult = otherResult.PurchaseResult;
								var otherItems = otherResult.Items;

								switch (otherPurchaseResult) {
									case ArchiHandler.PurchaseResponseCallback.EPurchaseResult.OK:
										alreadyHandled = true; // We're done with this key
										response.Append(Environment.NewLine + "<" + bot.BotName + "> Key: " + key + " | Status: " + otherPurchaseResult + " | Items: " + string.Join("", otherItems));
										break;
									case ArchiHandler.PurchaseResponseCallback.EPurchaseResult.DuplicatedKey:
									case ArchiHandler.PurchaseResponseCallback.EPurchaseResult.InvalidKey:
										alreadyHandled = true; // This key doesn't work, don't try to redeem it anymore
										response.Append(Environment.NewLine + "<" + bot.BotName + "> Key: " + key + " | Status: " + otherPurchaseResult + " | Items: " + string.Join("", otherItems));
										break;
									default:
										response.Append(Environment.NewLine + "<" + bot.BotName + "> Key: " + key + " | Status: " + otherPurchaseResult + " | Items: " + string.Join("", otherItems));
										break;
								}
							}
							key = reader.ReadLine();
							break;
						case ArchiHandler.PurchaseResponseCallback.EPurchaseResult.OK:
							response.Append(Environment.NewLine + "<" + currentBot.BotName + "> Key: " + key + " | Status: " + purchaseResult + " | Items: " + string.Join("", items));
							if (DistributeKeys) {
								do {
									if (iterator.MoveNext()) {
										currentBot = iterator.Current;
									} else {
										currentBot = null;
									}
								} while (currentBot == this);
							}
							key = reader.ReadLine();
							break;
						case ArchiHandler.PurchaseResponseCallback.EPurchaseResult.DuplicatedKey:
						case ArchiHandler.PurchaseResponseCallback.EPurchaseResult.InvalidKey:
							response.Append(Environment.NewLine + "<" + currentBot.BotName + "> Key: " + key + " | Status: " + purchaseResult + " | Items: " + string.Join("", items));
							if (DistributeKeys && !ForwardKeysToOtherBots) {
								do {
									if (iterator.MoveNext()) {
										currentBot = iterator.Current;
									} else {
										currentBot = null;
									}
								} while (currentBot == this);
							}
							key = reader.ReadLine();
							break;
					}
				}
			}

			if (response.Length == 0) {
				return null;
			}

			return response.ToString();
		}

		private static async Task<string> ResponseRedeem(string botName, string message, bool validate) {
			if (string.IsNullOrEmpty(botName) || string.IsNullOrEmpty(message)) {
				return null;
			}

			Bot bot;
			if (!Bots.TryGetValue(botName, out bot)) {
				return "Couldn't find any bot named " + botName + "!";
			}

			return await bot.ResponseRedeem(message, validate).ConfigureAwait(false);
		}

		private static string ResponseRejoinChat() {
			foreach (Bot bot in Bots.Values) {
				bot.JoinMasterChat();
			}

			return "Done!";
		}

		private async Task<string> ResponseAddLicense(HashSet<uint> gameIDs) {
			if (gameIDs == null || gameIDs.Count == 0) {
				return null;
			}

			StringBuilder result = new StringBuilder();
			foreach (uint gameID in gameIDs) {
				SteamApps.FreeLicenseCallback callback;
				try {
					callback = await SteamApps.RequestFreeLicense(gameID);
				} catch (Exception e) {
					Logging.LogGenericException(e, BotName);
					continue;
				}

				result.AppendLine("Result: " + callback.Result + " | Granted apps: " + string.Join(", ", callback.GrantedApps) + " " + string.Join(", ", callback.GrantedPackages));
			}

			return result.ToString();
		}

		private static async Task<string> ResponseAddLicense(string botName, string games) {
			if (string.IsNullOrEmpty(botName) || string.IsNullOrEmpty(games)) {
				return null;
			}

			Bot bot;
			if (!Bots.TryGetValue(botName, out bot)) {
				return "Couldn't find any bot named " + botName + "!";
			}

			string[] gameIDs = games.Split(',');

			HashSet<uint> gamesToRedeem = new HashSet<uint>();
			foreach (string game in gameIDs) {
				uint gameID;
				if (!uint.TryParse(game, out gameID)) {
					continue;
				}
				gamesToRedeem.Add(gameID);
			}

			if (gamesToRedeem.Count == 0) {
				return "Couldn't parse any games given!";
			}

			return await bot.ResponseAddLicense(gamesToRedeem).ConfigureAwait(false);
		}

		private async Task<string> ResponsePlay(HashSet<uint> gameIDs) {
			if (gameIDs == null || gameIDs.Count == 0) {
				return null;
			}

			if (gameIDs.Contains(0)) {
				if (await CardsFarmer.SwitchToManualMode(false).ConfigureAwait(false)) {
					ResetGamesPlayed();
				}
			} else {
				await CardsFarmer.SwitchToManualMode(true).ConfigureAwait(false);
				ArchiHandler.PlayGames(gameIDs);
			}

			return "Done!";
		}

		private static async Task<string> ResponsePlay(string botName, string games) {
			if (string.IsNullOrEmpty(botName) || string.IsNullOrEmpty(games)) {
				return null;
			}

			Bot bot;
			if (!Bots.TryGetValue(botName, out bot)) {
				return "Couldn't find any bot named " + botName + "!";
			}

			string[] gameIDs = games.Split(',');

			HashSet<uint> gamesToPlay = new HashSet<uint>();
			foreach (string game in gameIDs) {
				uint gameID;
				if (!uint.TryParse(game, out gameID)) {
					continue;
				}
				gamesToPlay.Add(gameID);
			}

			if (gamesToPlay.Count == 0) {
				return "Couldn't parse any games given!";
			}

			return await bot.ResponsePlay(gamesToPlay).ConfigureAwait(false);
		}

		private async Task<string> ResponseStart() {
			if (KeepRunning) {
				return "That bot instance is already running!";
			}

			await Start().ConfigureAwait(false);
			return "Done!";
		}

		private static async Task<string> ResponseStart(string botName) {
			if (string.IsNullOrEmpty(botName)) {
				return null;
			}

			Bot bot;
			if (!Bots.TryGetValue(botName, out bot)) {
				return "Couldn't find any bot named " + botName + "!";
			}

			return await bot.ResponseStart().ConfigureAwait(false);
		}

		private string ResponseStop() {
			if (!KeepRunning) {
				return "That bot instance is already inactive!";
			}

			Shutdown();
			return "Done!";
		}

		private static string ResponseStop(string botName) {
			if (string.IsNullOrEmpty(botName)) {
				return null;
			}

			Bot bot;
			if (!Bots.TryGetValue(botName, out bot)) {
				return "Couldn't find any bot named " + botName + "!";
			}

			return bot.ResponseStop();
		}

		private void HandleCallbacks() {
			TimeSpan timeSpan = TimeSpan.FromMilliseconds(CallbackSleep);
			while (KeepRunning) {
				CallbackManager.RunWaitCallbacks(timeSpan);
			}
		}

		private async Task HandleMessage(ulong steamID, string message) {
			if (steamID == 0 || string.IsNullOrEmpty(message)) {
				return;
			}

			SendMessage(steamID, await HandleMessage(message).ConfigureAwait(false));
		}

		private void SendMessage(ulong steamID, string message) {
			if (steamID == 0 || string.IsNullOrEmpty(message)) {
				return;
			}

			// TODO: I really need something better
			if (steamID < 110300000000000000) {
				SteamFriends.SendChatMessage(steamID, EChatEntryType.ChatMsg, message);
			} else {
				SteamFriends.SendChatRoomMessage(steamID, EChatEntryType.ChatMsg, message);
			}
		}

		private bool LinkMobileAuthenticator() {
			if (SteamGuardAccount != null) {
				return false;
			}

			Logging.LogGenericInfo("Linking new ASF MobileAuthenticator...", BotName);
			UserLogin userLogin = new UserLogin(SteamLogin, SteamPassword);
			LoginResult loginResult;
			while ((loginResult = userLogin.DoLogin()) != LoginResult.LoginOkay) {
				switch (loginResult) {
					case LoginResult.NeedEmail:
						userLogin.EmailCode = Program.GetUserInput(BotName, Program.EUserInputType.SteamGuard);
						break;
					default:
						Logging.LogGenericError("Unhandled situation: " + loginResult, BotName);
						return false;
				}
			}

			AuthenticatorLinker authenticatorLinker = new AuthenticatorLinker(userLogin.Session);

			AuthenticatorLinker.LinkResult linkResult;
			while ((linkResult = authenticatorLinker.AddAuthenticator()) != AuthenticatorLinker.LinkResult.AwaitingFinalization) {
				switch (linkResult) {
					case AuthenticatorLinker.LinkResult.MustProvidePhoneNumber:
						authenticatorLinker.PhoneNumber = Program.GetUserInput(BotName, Program.EUserInputType.PhoneNumber);
						break;
					default:
						Logging.LogGenericError("Unhandled situation: " + linkResult, BotName);
						return false;
				}
			}

			SteamGuardAccount = authenticatorLinker.LinkedAccount;

			try {
				File.WriteAllText(MobileAuthenticatorFile, JsonConvert.SerializeObject(SteamGuardAccount));
			} catch (Exception e) {
				Logging.LogGenericException(e, BotName);
				return false;
			}

			AuthenticatorLinker.FinalizeResult finalizeResult = authenticatorLinker.FinalizeAddAuthenticator(Program.GetUserInput(BotName, Program.EUserInputType.SMS));
			if (finalizeResult != AuthenticatorLinker.FinalizeResult.Success) {
				Logging.LogGenericError("Unhandled situation: " + finalizeResult, BotName);
				DelinkMobileAuthenticator();
				return false;
			}

			Logging.LogGenericInfo("Successfully linked ASF as new mobile authenticator for this account!", BotName);
			Program.GetUserInput(BotName, Program.EUserInputType.RevocationCode, SteamGuardAccount.RevocationCode);
			return true;
		}

		private bool DelinkMobileAuthenticator() {
			if (SteamGuardAccount == null) {
				return false;
			}

			bool result = SteamGuardAccount.DeactivateAuthenticator();
			SteamGuardAccount = null;

			try {
				File.Delete(MobileAuthenticatorFile);
			} catch (Exception e) {
				Logging.LogGenericException(e, BotName);
			}

			return result;
		}

		private void JoinMasterChat() {
			if (SteamMasterClanID == 0) {
				return;
			}

			SteamFriends.JoinChat(SteamMasterClanID);
		}

		private void OnConnected(SteamClient.ConnectedCallback callback) {
			if (callback == null) {
				return;
			}

			if (callback.Result != EResult.OK) {
				Logging.LogGenericError("Unable to connect to Steam: " + callback.Result, BotName);
				return;
			}

			Logging.LogGenericInfo("Connected to Steam!", BotName);

			if (File.Exists(LoginKeyFile)) {
				try {
					LoginKey = File.ReadAllText(LoginKeyFile);
				} catch (Exception e) {
					Logging.LogGenericException(e, BotName);
				}
			}

			byte[] sentryHash = null;
			if (File.Exists(SentryFile)) {
				try {
					byte[] sentryFileContent = File.ReadAllBytes(SentryFile);
					sentryHash = CryptoHelper.SHAHash(sentryFileContent);
				} catch (Exception e) {
					Logging.LogGenericException(e, BotName);
				}
			}

			if (SteamLogin.Equals("null")) {
				SteamLogin = Program.GetUserInput(BotName, Program.EUserInputType.Login);
			}

			if (SteamPassword.Equals("null") && string.IsNullOrEmpty(LoginKey)) {
				SteamPassword = Program.GetUserInput(BotName, Program.EUserInputType.Password);
			}

			SteamUser.LogOn(new SteamUser.LogOnDetails {
				Username = SteamLogin,
				Password = SteamPassword,
				AuthCode = AuthCode,
				LoginID = LoginID,
				LoginKey = LoginKey,
				TwoFactorCode = TwoFactorAuth,
				SentryFileHash = sentryHash,
				ShouldRememberPassword = true
			});
		}

		private async void OnDisconnected(SteamClient.DisconnectedCallback callback) {
			if (callback == null) {
				return;
			}

			Logging.LogGenericInfo("Disconnected from Steam!", BotName);
			await CardsFarmer.StopFarming().ConfigureAwait(false);

			if (!KeepRunning) {
				return;
			}

			// If we initiated disconnect, do not attempt to reconnect
			if (callback.UserInitiated) {
				return;
			}

			if (InvalidPassword) {
				InvalidPassword = false;
				if (!string.IsNullOrEmpty(LoginKey)) { // InvalidPassword means usually that login key has expired, if we used it
					LoginKey = null;

					try {
						File.Delete(LoginKeyFile);
					} catch (Exception e) {
						Logging.LogGenericException(e, BotName);
					}

					Logging.LogGenericInfo("Removed expired login key", BotName);
				} else { // If we didn't use login key, InvalidPassword usually means we got captcha or other network-based throttling
					Logging.LogGenericInfo("Will retry after 25 minutes...", BotName);
					await Utilities.SleepAsync(25 * 60 * 1000).ConfigureAwait(false); // Captcha disappears after around 20 minutes, so we make it 25
				}
			} else if (LoggedInElsewhere) {
				LoggedInElsewhere = false;
				Logging.LogGenericWarning("Account is being used elsewhere, will try reconnecting in 30 minutes...", BotName);
				await Utilities.SleepAsync(30 * 60 * 1000).ConfigureAwait(false);
			}

			Logging.LogGenericInfo("Reconnecting...", BotName);

			// 2FA tokens are expiring soon, use limiter only when we don't have any pending
			if (TwoFactorAuth == null) {
				await Program.LimitSteamRequestsAsync().ConfigureAwait(false);
			}

			SteamClient.Connect();
		}

		private void OnFreeLicense(SteamApps.FreeLicenseCallback callback) {
			if (callback == null) {
				return;
			}
		}

		private void OnChatInvite(SteamFriends.ChatInviteCallback callback) {
			if (callback == null) {
				return;
			}

			if (callback.PatronID != SteamMasterID) {
				return;
			}

			SteamFriends.JoinChat(callback.ChatRoomID);
		}

		private async void OnChatMsg(SteamFriends.ChatMsgCallback callback) {
			if (callback == null) {
				return;
			}

			if (callback.ChatMsgType != EChatEntryType.ChatMsg) {
				return;
			}

			if (callback.ChatterID != SteamMasterID) {
				return;
			}

			switch (callback.Message) {
				case "!leave":
					SteamFriends.LeaveChat(callback.ChatRoomID);
					break;
				default:
					await HandleMessage(callback.ChatRoomID, callback.Message).ConfigureAwait(false);
					break;
			}
		}

		private void OnFriendsList(SteamFriends.FriendsListCallback callback) {
			if (callback == null) {
				return;
			}

			foreach (var friend in callback.FriendList) {
				if (friend.Relationship != EFriendRelationship.RequestRecipient) {
					continue;
				}

				switch (friend.SteamID.AccountType) {
					case EAccountType.Clan:
						// TODO: Accept clan invites from master?
						break;
					default:
						if (friend.SteamID == SteamMasterID) {
							SteamFriends.AddFriend(friend.SteamID);
						}
						break;
				}
			}
		}

		private async void OnFriendMsg(SteamFriends.FriendMsgCallback callback) {
			if (callback == null) {
				return;
			}

			if (callback.EntryType != EChatEntryType.ChatMsg) {
				return;
			}

			if (callback.Sender != SteamMasterID) {
				return;
			}

			await HandleMessage(callback.Sender, callback.Message).ConfigureAwait(false);
		}

		private async void OnFriendMsgHistory(SteamFriends.FriendMsgHistoryCallback callback) {
			if (callback == null) {
				return;
			}

			if (callback.Result != EResult.OK) {
				return;
			}

			if (callback.SteamID != SteamMasterID) {
				return;
			}

			if (callback.Messages.Count == 0) {
				return;
			}

			// Get last message
			var lastMessage = callback.Messages[callback.Messages.Count - 1];

			// If message is read already, return
			if (!lastMessage.Unread) {
				return;
			}

			// If message is too old, return
			if (DateTime.UtcNow.Subtract(lastMessage.Timestamp).TotalMinutes > 1) {
				return;
			}

			// Handle the message
			await HandleMessage(callback.SteamID, lastMessage.Message).ConfigureAwait(false);
		}

		private void OnAccountInfo(SteamUser.AccountInfoCallback callback) {
			if (callback == null) {
				return;
			}

			if (!FarmOffline) {
				SteamFriends.SetPersonaState(EPersonaState.Online);
			}
		}

		private void OnLoggedOff(SteamUser.LoggedOffCallback callback) {
			if (callback == null) {
				return;
			}

			Logging.LogGenericInfo("Logged off of Steam: " + callback.Result, BotName);

			switch (callback.Result) {
				case EResult.AlreadyLoggedInElsewhere:
				case EResult.LoggedInElsewhere:
				case EResult.LogonSessionReplaced:
					LoggedInElsewhere = true;
					break;
			}
		}

		private async void OnLoggedOn(SteamUser.LoggedOnCallback callback) {
			if (callback == null) {
				return;
			}

			switch (callback.Result) {
				case EResult.AccountLogonDenied:
					AuthCode = Program.GetUserInput(SteamLogin, Program.EUserInputType.SteamGuard);
					break;
				case EResult.AccountLoginDeniedNeedTwoFactor:
					if (SteamGuardAccount == null) {
						TwoFactorAuth = Program.GetUserInput(SteamLogin, Program.EUserInputType.TwoFactorAuthentication);
					} else {
						TwoFactorAuth = SteamGuardAccount.GenerateSteamGuardCode();
					}
					break;
				case EResult.InvalidPassword:
					InvalidPassword = true;
					Logging.LogGenericWarning("Unable to login to Steam: " + callback.Result, BotName);
					break;
				case EResult.OK:
					Logging.LogGenericInfo("Successfully logged on!", BotName);

					if (UseAsfAsMobileAuthenticator && TwoFactorAuth == null && SteamGuardAccount == null) {
						LinkMobileAuthenticator();
					}

					// Reset one-time-only access tokens
					AuthCode = null;
					TwoFactorAuth = null;

					if (!SteamNickname.Equals("null")) {
						await SteamFriends.SetPersonaName(SteamNickname);
					}

					ResetGamesPlayed();

					if (SteamParentalPIN.Equals("null")) {
						SteamParentalPIN = Program.GetUserInput(BotName, Program.EUserInputType.SteamParentalPIN);
					}

					if (!await ArchiWebHandler.Init(SteamClient, callback.WebAPIUserNonce, SteamParentalPIN).ConfigureAwait(false)) {
						await Restart().ConfigureAwait(false);
						return;
					}

					if (SteamMasterClanID != 0) {
						await ArchiWebHandler.JoinClan(SteamMasterClanID).ConfigureAwait(false);
						JoinMasterChat();
					}

					if (Statistics) {
						await ArchiWebHandler.JoinClan(ArchiSCFarmGroup).ConfigureAwait(false);
						SteamFriends.JoinChat(ArchiSCFarmGroup);
					}

					Trading.CheckTrades();

					var start = Task.Run(async () => await CardsFarmer.StartFarming().ConfigureAwait(false));
					break;
				case EResult.NoConnection:
				case EResult.ServiceUnavailable:
				case EResult.Timeout:
				case EResult.TryAnotherCM:
					Logging.LogGenericWarning("Unable to login to Steam: " + callback.Result, BotName);
					break;
				default: // Unexpected result, shutdown immediately
					Logging.LogGenericWarning("Unable to login to Steam: " + callback.Result, BotName);
					Shutdown();
					break;
			}
		}

		private void OnLoginKey(SteamUser.LoginKeyCallback callback) {
			if (callback == null) {
				return;
			}

			try {
				File.WriteAllText(LoginKeyFile, callback.LoginKey);
			} catch (Exception e) {
				Logging.LogGenericException(e, BotName);
			}

			SteamUser.AcceptNewLoginKey(callback);
		}

		private void OnMachineAuth(SteamUser.UpdateMachineAuthCallback callback) {
			if (callback == null) {
				return;
			}

			try {
				int fileSize;
				byte[] sentryHash;

				using (FileStream fileStream = File.Open(SentryFile, FileMode.OpenOrCreate, FileAccess.ReadWrite)) {
					fileStream.Seek(callback.Offset, SeekOrigin.Begin);
					fileStream.Write(callback.Data, 0, callback.BytesToWrite);
					fileSize = (int) fileStream.Length;

					fileStream.Seek(0, SeekOrigin.Begin);
					using (SHA1CryptoServiceProvider sha = new SHA1CryptoServiceProvider()) {
						sentryHash = sha.ComputeHash(fileStream);
					}
				}

				// Inform the steam servers that we're accepting this sentry file
				SteamUser.SendMachineAuthResponse(new SteamUser.MachineAuthDetails {
					JobID = callback.JobID,
					FileName = callback.FileName,
					BytesWritten = callback.BytesToWrite,
					FileSize = fileSize,
					Offset = callback.Offset,
					Result = EResult.OK,
					LastError = 0,
					OneTimePassword = callback.OneTimePassword,
					SentryFileHash = sentryHash,
				});
			} catch (Exception e) {
				Logging.LogGenericException(e, BotName);
			}
		}

		private async void OnNotifications(ArchiHandler.NotificationsCallback callback) {
			if (callback == null || callback.Notifications == null) {
				return;
			}

			bool checkTrades = false;
			bool markInventory = false;
			foreach (var notification in callback.Notifications) {
				switch (notification.NotificationType) {
					case ArchiHandler.NotificationsCallback.Notification.ENotificationType.Items:
						markInventory = true;
						break;
					case ArchiHandler.NotificationsCallback.Notification.ENotificationType.Trading:
						checkTrades = true;
						break;
				}
			}

			if (checkTrades) {
				Trading.CheckTrades();
			}

			if (markInventory) {
				await ArchiWebHandler.MarkInventory().ConfigureAwait(false);
			}
		}

		private void OnOfflineMessage(ArchiHandler.OfflineMessageCallback callback) {
			if (callback == null) {
				return;
			}

			if (!HandleOfflineMessages) {
				return;
			}

			SteamFriends.RequestOfflineMessages();
		}

		private async void OnPurchaseResponse(ArchiHandler.PurchaseResponseCallback callback) {
			if (callback == null) {
				return;
			}

			if (callback.PurchaseResult == ArchiHandler.PurchaseResponseCallback.EPurchaseResult.OK) {
				// We will restart CF module to recalculate current status and decide about new optimal approach
				await CardsFarmer.RestartFarming().ConfigureAwait(false);
			}
		}

		private bool ReadConfig() {
			if (!File.Exists(ConfigFile)) {
				return false;
			}

			try {
				using (XmlReader reader = XmlReader.Create(ConfigFile)) {
					while (reader.Read()) {
						if (reader.NodeType != XmlNodeType.Element) {
							continue;
						}

						string key = reader.Name;
						if (string.IsNullOrEmpty(key)) {
							continue;
						}

						string value = reader.GetAttribute("value");
						if (string.IsNullOrEmpty(value)) {
							continue;
						}

						switch (key) {
							case "Enabled":
								Enabled = bool.Parse(value);
								break;
							case "SteamLogin":
								SteamLogin = value;
								break;
							case "SteamPassword":
								SteamPassword = value;
								break;
							case "SteamNickname":
								SteamNickname = value;
								break;
							case "SteamApiKey":
								SteamApiKey = value;
								break;
							case "SteamTradeToken":
								SteamTradeToken = value;
								break;
							case "SteamParentalPIN":
								SteamParentalPIN = value;
								break;
							case "SteamMasterID":
								SteamMasterID = ulong.Parse(value);
								break;
							case "SteamMasterClanID":
								SteamMasterClanID = ulong.Parse(value);
								break;
							case "StartOnLaunch":
								StartOnLaunch = bool.Parse(value);
								break;
							case "UseAsfAsMobileAuthenticator":
								UseAsfAsMobileAuthenticator = bool.Parse(value);
								break;
							case "CardDropsRestricted":
								CardDropsRestricted = bool.Parse(value);
								break;
							case "FarmOffline":
								FarmOffline = bool.Parse(value);
								break;
							case "HandleOfflineMessages":
								HandleOfflineMessages = bool.Parse(value);
								break;
							case "ForwardKeysToOtherBots":
								ForwardKeysToOtherBots = bool.Parse(value);
								break;
							case "DistributeKeys":
								DistributeKeys = bool.Parse(value);
								break;
							case "ShutdownOnFarmingFinished":
								ShutdownOnFarmingFinished = bool.Parse(value);
								break;
							case "SendOnFarmingFinished":
								SendOnFarmingFinished = bool.Parse(value);
								break;
							case "SendTradePeriod":
								SendTradePeriod = byte.Parse(value);
								break;
							case "Blacklist":
								Blacklist.Clear();
								foreach (string appID in value.Split(',')) {
									Blacklist.Add(uint.Parse(appID));
								}
								break;
							case "GamesPlayedWhileIdle":
								GamesPlayedWhileIdle.Clear();
								foreach (string appID in value.Split(',')) {
									GamesPlayedWhileIdle.Add(uint.Parse(appID));
								}
								break;
							case "Statistics":
								Statistics = bool.Parse(value);
								break;
							default:
								Logging.LogGenericWarning("Unrecognized config value: " + key + "=" + value, BotName);
								break;
						}
					}
				}
			} catch (Exception e) {
				Logging.LogGenericException(e, BotName);
				Logging.LogGenericError("Your config for this bot instance is invalid, it won't run!", BotName);
				return false;
			}

			return true;
		}
	}
}
