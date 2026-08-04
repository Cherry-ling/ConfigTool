// Explicit configuration links. `sources` is optional: omit it for a shared
// field convention, or provide sheet names when the same field means different
// things in different configuration domains.
window.CONFIG_RELATION_RULES = [
  // FFM album: verified from AlbumData.lua, AlbumSetUI.lua and RewardApplier.lua.
  {
    sources: ["Album", "AlbumB"],
    fields: ["setId"],
    targets: ["AlbumSet"],
    targetKey: "ID",
    mode: "list",
    label: "卡组"
  },
  {
    sources: ["Album", "AlbumB"],
    fields: ["reward", "startReward"],
    targets: ["RewardPack"],
    targetKey: "ID",
    mode: "list",
    label: "奖励包"
  },
  {
    sources: ["AlbumSet"],
    fields: ["cardId"],
    targets: ["AlbumCard"],
    targetKey: "ID",
    mode: "list",
    label: "卡片"
  },
  {
    sources: ["AlbumSet"],
    fields: ["reward"],
    targets: ["RewardPack"],
    targetKey: "ID",
    mode: "list",
    label: "卡组奖励"
  },
  {
    sources: ["AlbumCard"],
    fields: ["setID", "setId"],
    targets: ["AlbumSet"],
    targetKey: "ID",
    mode: "scalar",
    label: "所属卡组"
  },
  {
    sources: ["AlbumCard"],
    fields: ["rewards"],
    targets: ["item"],
    targetKey: "ID",
    mode: "tuple",
    tupleIndex: 0,
    label: "奖励物品"
  },
  {
    sources: ["AlbumCardPack"],
    fields: ["replacement"],
    targets: ["item"],
    targetKey: "ID",
    mode: "tuple",
    tupleIndex: 0,
    label: "替换奖励物品"
  },
  {
    sources: ["AlbumBox"],
    fields: ["reward"],
    targets: ["RewardPack"],
    targetKey: "ID",
    mode: "scalar",
    label: "宝箱奖励包"
  },
  {
    sources: ["AlbumBox"],
    fields: ["cardPiece"],
    targets: ["item"],
    targetKey: "ID",
    mode: "scalar",
    label: "卡片碎片"
  },

  // FFM reward tuples: [itemId, count, time, ...].
  {
    sources: [
      "RewardPack", "Box", "AlbumCard", "ProgressPackItem", "BMMCollection",
      "BigMapMergeTask", "BigMapPiece", "BakeRoomBox", "BakeRoomItem",
      "DogHouseCollection", "DogHouseItem", "SlotMachineElement", "SlotMachineWeight",
      "SlotMachineNewbieWeight", "SlotMachineReward", "SlotMachineRewardB",
      "SlotMachineRewardC", "SlotMachineRewardD", "PuzzleTimeEvent", "TreasureHuntBoard",
      "TreasureHuntBoardB", "TreasureHuntBoardC", "TreasureHuntBoardD"
    ],
    fields: ["reward", "rewards", "rewards_juiceparty", "freeRewards", "plusRewards", "replacement"],
    targets: ["item"],
    targetKey: "ID",
    mode: "tuple",
    tupleIndex: 0,
    label: "奖励物品"
  },
  {
    sources: ["Box"],
    fields: ["viewId"],
    targets: ["BoxView"],
    targetKey: "ID",
    mode: "scalar",
    label: "宝箱展示"
  },

  // FFM activities, stores and progression.
  {
    sources: ["Tournament"],
    fields: ["rankReward", "milestoneReward"],
    targets: ["TournamentReward"],
    targetKey: "ID",
    mode: "scalar",
    label: "锦标赛奖励"
  },
  {
    sources: ["TournamentReward", "TournamentRewardB"],
    fields: ["content", "content_juiceparty"],
    targets: ["RewardPack"],
    targetKey: "ID",
    mode: "tuple",
    tupleIndex: 1,
    label: "奖励包"
  },
  {
    sources: ["ProgressPackEvent"],
    fields: ["shopId"],
    targets: ["shop"],
    targetKey: "ID",
    mode: "scalar",
    label: "商品"
  },
  {
    sources: ["ProgressPackEvent"],
    fields: ["itemIds"],
    targets: ["ProgressPackItem"],
    targetKey: "ID",
    mode: "list",
    label: "成长基金任务"
  },
  {
    sources: ["ShopLevelStagePack"],
    fields: ["shop"],
    targets: ["shop"],
    targetKey: "ID",
    mode: "list",
    label: "阶段商品"
  },
  {
    sources: ["shopRecommend", "shopRecommendBak", "WeekendPack", "Copy of shopRecommend"],
    fields: ["packageIds", "packageIds1", "packageIds2", "coinIds1", "coinIds2", "piggyBankShopId", "goldenTicketShopId"],
    targets: ["shop"],
    targetKey: "ID",
    mode: "list",
    label: "商品"
  },
  {
    sources: ["shopRecommend", "shopRecommendBak"],
    fields: ["progressPackShopId"],
    targets: ["shop"],
    targetKey: "ID",
    mode: "scalar",
    label: "成长基金商品"
  },
  {
    sources: ["shopRecommend", "shopRecommendBak"],
    fields: ["progressPackItemIDs"],
    targets: ["ProgressPackItem"],
    targetKey: "ID",
    mode: "list",
    label: "成长基金任务"
  },
  {
    sources: ["area"],
    fields: ["landId"],
    targets: ["areaLand"],
    targetKey: "ID",
    mode: "scalar",
    label: "大地图"
  },
  {
    sources: ["area"],
    fields: ["unlockTaskId"],
    targets: ["task"],
    targetKey: "ID",
    mode: "scalar",
    label: "场景任务"
  },
  {
    sources: ["area"],
    fields: ["reward"],
    targets: ["shop"],
    targetKey: "ID",
    mode: "scalar",
    label: "场景奖励商品"
  },
  {
    sources: ["areaLand"],
    fields: ["areaIds"],
    targets: ["area"],
    targetKey: "ID",
    mode: "list",
    label: "场景"
  },
  {
    sources: ["task"],
    fields: ["premise"],
    targets: ["task"],
    targetKey: "ID",
    mode: "list",
    label: "前置任务"
  },

  // Shared FFM field conventions.
  {
    fields: ["itemId", "itemID", "collectItemId", "item1id", "item2id", "item3id", "item4id", "item5id", "item6id", "item7id", "item8id"],
    targets: ["item"],
    targetKey: "ID",
    mode: "scalar",
    label: "物品"
  },
  {
    fields: ["shopId", "ShopID", "ShopId"],
    targets: ["shop"],
    targetKey: "ID",
    mode: "scalar",
    label: "商品"
  },
  {
    fields: ["level", "unlockLevel", "unlock_level", "locked_level", "startLevel", "endLevel", "levelMin", "levelMax"],
    targets: ["level"],
    targetKey: "ID",
    mode: "scalar",
    label: "关卡"
  },
  {
    fields: ["areaID", "areaId"],
    targets: ["area"],
    targetKey: "ID",
    mode: "scalar",
    label: "场景"
  },
  {
    fields: ["taskID", "taskId"],
    targets: ["task"],
    targetKey: "ID",
    mode: "scalar",
    label: "任务"
  },
  {
    fields: ["lang", "langKey", "langKeyLong", "descLang", "msgId", "tipMsgId", "guideMsg", "title_key", "content_key"],
    targets: ["Lang"],
    targetKey: "key",
    mode: "scalar",
    label: "多语言"
  },

  // Existing PairPair rules.
  {
    fields: ["PhaseReward", "PhaseRewardID"],
    targets: ["PhaseReward"],
    targetKey: "ID",
    mode: "scalar",
    label: "阶段奖励"
  },
  {
    fields: ["PaymentID", "PaymentId"],
    targets: ["Payment"],
    targetKey: "ID",
    mode: "scalar",
    label: "商品"
  },
  {
    fields: ["RankReward", "RankRewardID", "RankRewardId"],
    targets: ["RankReward"],
    targetKey: "ID",
    mode: "scalar",
    label: "排名奖励"
  },
  {
    fields: ["TargetItem", "TargetItemID", "TargetItemId", "ItemID", "ItemId", "itemId"],
    targets: ["Item", "ItemCfg", "item@design"],
    targetKey: "ID",
    mode: "scalar",
    label: "物品"
  },
  {
    fields: ["LevelID", "LevelId", "UnlockLevelID", "UnlockLevelId"],
    targets: ["Level", "level@design"],
    targetKey: "ID",
    mode: "scalar",
    label: "关卡"
  },
  {
    fields: ["ProfileID", "ProfileId"],
    targets: ["Profile", "ProfileCfg"],
    targetKey: "ID",
    mode: "scalar",
    label: "头像"
  },
  {
    fields: ["BotID", "BotId"],
    targets: ["Bot"],
    targetKey: "ID",
    mode: "scalar",
    label: "机器人"
  },
  {
    fields: ["Reward", "FreeReward", "PayerReward", "BattlePassFreeReward", "ExReward1", "RewardTargetItem", "CollectItemCount"],
    targets: ["Item", "ItemCfg", "item@design"],
    targetKey: "ID",
    mode: "jsonKeys",
    label: "奖励物品"
  }
];
