// Exercise the real UI relation engine without booting the DOM/native bridge.
// Usage: node Tests/json_relations_ui.test.cjs <rawdata directory>
const fs = require("node:fs");
const path = require("node:path");
const vm = require("node:vm");
const assert = require("node:assert/strict");
const root = path.resolve(__dirname, "..");
const context = { window: {}, localStorage: { getItem: () => null, setItem() {} } };
vm.createContext(context);
vm.runInContext(fs.readFileSync(path.join(root, "Resources/relationships.js"), "utf8"), context);
let app = fs.readFileSync(path.join(root, "Resources/app.js"), "utf8");
app = app.slice(0, app.lastIndexOf("\n  bindElements();")) + `
  window.testEngine = {state, resolveFieldHeaderRowIndex, fieldHeaders, columnMetadata, relationRuleForColumn, relationDestinations, matchingRelationRows, reverseQueryPlan, reverseReferenceTargetsBook};
})();`;
vm.runInContext(app, context);
const engine = context.window.testEngine;
const books = fs.readdirSync(process.argv[2]).filter(file => file.endsWith(".json")).flatMap(file => {
  const data = JSON.parse(fs.readFileSync(path.join(process.argv[2], file), "utf8"));
  return Object.entries(data).filter(([key]) => /(?:Map|Slice)$/.test(key)).map(([sheet, table]) => {
    const records = Array.isArray(table) ? table : Object.keys(table).sort().map(key => table[key]);
    const keys = Array.isArray(table) ? table.map((_, index) => String(index + 1)) : Object.keys(table).sort();
    const fields = [...new Set(records.flatMap(row => Object.keys(row)))].sort((a, b) =>
      Number(!/^(id|key)$/i.test(a)) - Number(!/^(id|key)$/i.test(b)) || (a < b ? -1 : a > b ? 1 : 0));
    const rows = [["键 / 序号", ...fields.map(() => "")], ["__key", ...fields], ["string", ...fields.map(() => "string")],
      ...records.map((record, index) => [keys[index], ...fields.map(field => typeof record[field] === "string" ? record[field] : JSON.stringify(record[field]) ?? "")])];
    return { id: file + "::" + sheet, name: file.slice(0, -5), fileName: file, sheetName: sheet, sheetCount: 1, sourceKind: "json", isLoaded: true, columnCount: fields.length + 1, rows };
  });
});
engine.state.payload = { workbooks: books };
function book(name) { return books.find(book => book.name === name); }
function destinations(name, key, field) {
  const source = book(name);
  const row = source.rows.findIndex((row, index) => index >= 3 && row[0] === key);
  const column = engine.fieldHeaders(source).indexOf(field);
  assert.ok(row >= 3 && column > 0, `${name} ${key} ${field}`);
  const rule = engine.relationRuleForColumn(source, column);
  return engine.relationDestinations(source, row, column, source.rows[row][column], rule)
    .filter(destination => engine.matchingRelationRows(books.find(book => book.id === destination.bookId), destination));
}
const lang = book("LangConf");
assert.equal(engine.resolveFieldHeaderRowIndex(lang), 1, "locale columns en/es must not be mistaken for type headers");
assert.ok(destinations("ActivityConf", "100", "desc").some(item => item.bookId.startsWith("LangConf.json")));
assert.ok(destinations("ActivityCatalogConf", "700", "id").some(item => item.bookId.startsWith("ActivityConf.json")));
assert.ok(destinations("DailyMissionItemConf", "1", "type").some(item => item.key === "type"));
const journey = destinations("JourneyRewardConf", "1", "reward");
assert.ok(journey.some(item => item.bookId.startsWith("GoodsConf.json")) && journey.some(item => item.bookId.startsWith("JourneyBackgroundConf.json")));
const goods = book("GoodsConf");
const keyPlan = engine.reverseQueryPlan(goods, "key");
const idPlan = engine.reverseQueryPlan(goods, "id");
assert.ok(keyPlan.relationRules.some(rule => rule.mode === "rewardKeys"));
assert.ok(!idPlan.relationRules.some(rule => rule.mode === "rewardKeys"));
const action = book("DailyMissionActionConf");
assert.ok(engine.columnMetadata(action).identifierColumns[engine.fieldHeaders(action).indexOf("type")]);
assert.ok(engine.columnMetadata(lang).identifierColumns[engine.fieldHeaders(lang).indexOf("key")]);
console.log("json_headers=ok forward_lang=ok forward_primary_id=ok forward_type=ok reward_and_background=ok reverse_key_isolation=ok indexed_columns=ok");
