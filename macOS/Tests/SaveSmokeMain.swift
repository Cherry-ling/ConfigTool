import Foundation

struct WorkbookView: Codable {
    let id: String
    let name: String
    let fileName: String
    let sheetName: String
    let sheetCount: Int
    let category: String
    let modifiedAt: String
    let rowCount: Int
    let columnCount: Int
    let rows: [[String]]
    let sourceKind: String
    let sourceSignature: String
    let editableFromRow: Int
    let lockedCells: [String]
    let isLoaded: Bool
    let error: String?
}

struct CellChange: Codable {
    let row: Int
    let column: Int
    let value: String
}

enum ConfigCategory {
    static func name(for table: String) -> String { "测试" }
}

@main
struct SaveSmokeMain {
    static func main() throws {
        if CommandLine.arguments.count == 3 && CommandLine.arguments[1] == "--json-test" {
            try testJSON(fixture: URL(fileURLWithPath: CommandLine.arguments[2]))
            return
        }
        guard CommandLine.arguments.count == 3 else {
            throw NSError(domain: "SaveSmoke", code: 1, userInfo: [NSLocalizedDescriptionKey: "需要 Lua 与 Excel 临时目录"])
        }
        let luaDirectory = URL(fileURLWithPath: CommandLine.arguments[1], isDirectory: true)
        let excelDirectory = URL(fileURLWithPath: CommandLine.arguments[2], isDirectory: true)
        let saver = ConfigFileSaver()

        let luaURL = luaDirectory.appendingPathComponent("DailyQuestConf.lua")
        let luaSignature = configFileSignature(luaURL)
        try saver.save(
            directory: luaDirectory,
            id: "DailyQuestConf.lua::DailyQuestMap",
            expectedSignature: luaSignature,
            changes: [CellChange(row: 3, column: 2, value: "999")]
        )
        let parsedLua = try LuaConfigParser().parse(url: luaURL)[0]
        guard parsedLua.rows[3][2] == "999" else {
            throw NSError(domain: "SaveSmoke", code: 2, userInfo: [NSLocalizedDescriptionKey: "Lua 保存值未回读"])
        }
        do {
            try saver.save(
                directory: luaDirectory,
                id: "DailyQuestConf.lua::DailyQuestMap",
                expectedSignature: luaSignature,
                changes: [CellChange(row: 3, column: 2, value: "1000")]
            )
            throw NSError(domain: "SaveSmoke", code: 3, userInfo: [NSLocalizedDescriptionKey: "Lua 旧签名保存未被拒绝"])
        } catch ConfigSaveError.sourceChanged {
            // Expected.
        }

        let excelURL = excelDirectory.appendingPathComponent("Activity.xlsx")
        let excelSignature = configFileSignature(excelURL)
        try saver.save(
            directory: excelDirectory,
            id: "Activity.xlsx::Sheet1",
            expectedSignature: excelSignature,
            changes: [CellChange(row: 3, column: 0, value: "999999")]
        )
        print("lua_save=ok xlsx_save=ok conflict_guard=ok")
    }

    static func testJSON(fixture: URL) throws {
        let original = try Data(contentsOf: fixture)
        let books = try JsonConfigParser().parse(url: fixture)
        precondition(books.count == 3 && books.allSatisfy { $0.sheetCount == 3 && $0.editableFromRow == 3 })
        let map = books.first { $0.sheetName == "SampleMap" }!
        func cell(_ row: Int, _ field: String) -> String { map.rows[row][map.rows[1].firstIndex(of: field)!] }
        precondition(map.rowCount == 5 && cell(3, "name") == "中文配置")
        precondition(cell(3, "enabled") == "true" && cell(4, "enabled") == "false")
        precondition(cell(3, "big") == "9007199254740993")
        precondition(cell(3, "nullable") == "null" && cell(3, "empty").isEmpty)
        precondition(cell(3, "late_field").isEmpty && cell(4, "late_field") == "only in second row")
        let nested = try JSONSerialization.jsonObject(with: Data(cell(3, "nested").utf8)) as! NSDictionary
        precondition(nested.isEqual(to: ["Value": [1, false, NSNull()]]))
        precondition(books.first { $0.sheetName == "EmptySlice" }!.rowCount == 3)
        let slice = books.first { $0.sheetName == "SampleSlice" }!
        precondition(slice.rowCount == 5 && slice.rows[3][0] == "1" && slice.rows[4][0] == "2")
        precondition(map.lockedCells.contains("3:0") && map.lockedCells.contains("3:\(map.rows[1].firstIndex(of: "late_field")!)"))
        let after = try Data(contentsOf: fixture)
        precondition(after == original)
        print("json_map=ok json_slice=ok empty_table=ok nested_values=ok large_integer=ok json_editable_view=ok")
    }
}
