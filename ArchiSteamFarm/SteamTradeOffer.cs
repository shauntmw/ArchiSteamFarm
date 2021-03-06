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
using SteamKit2;
using System.Collections.Generic;

namespace ArchiSteamFarm {
	internal sealed class SteamTradeOffer {
		// REF: https://developer.valvesoftware.com/wiki/Steam_Web_API/IEconService#CEcon_TradeOffer
		internal enum ETradeOfferState : byte {
			Unknown,
			Invalid,
			Active,
			Accepted,
			Countered,
			Expired,
			Canceled,
			Declined,
			InvalidItems,
			EmailPending,
			EmailCanceled,
			OnHold
		}

		[JsonProperty(Required = Required.Always)]
		internal string tradeofferid { get; set; }

		[JsonProperty(Required = Required.Always)]
		internal int accountid_other { get; set; }

		[JsonProperty(Required = Required.Always)]
		internal ETradeOfferState trade_offer_state { get; set; }

		[JsonProperty(Required = Required.Always)]
		internal List<SteamItem> items_to_give { get; } = new List<SteamItem>();

		[JsonProperty(Required = Required.Always)]
		internal List<SteamItem> items_to_receive { get; } = new List<SteamItem>();

		// Extra
		private ulong _OtherSteamID64 = 0;
		internal ulong OtherSteamID64 {
			get {
				if (_OtherSteamID64 == 0 && accountid_other != 0) {
					_OtherSteamID64 = new SteamID((uint) accountid_other, EUniverse.Public, EAccountType.Individual).ConvertToUInt64();
				}

				return _OtherSteamID64;
			}
		}
	}
}
