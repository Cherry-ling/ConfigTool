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
}
