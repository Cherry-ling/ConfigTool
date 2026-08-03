window.CONFIG_RELATION_RULES = [
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
    fields: ["ShopID", "ShopId"],
    targets: ["Shop"],
    targetKey: "ID",
    mode: "scalar",
    label: "商店"
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
    fields: [
      "Reward", "FreeReward", "PayerReward", "BattlePassFreeReward",
      "ExReward1", "RewardTargetItem", "CollectItemCount"
    ],
    targets: ["Item", "ItemCfg", "item@design"],
    targetKey: "ID",
    mode: "jsonKeys",
    label: "奖励物品"
  }
];
