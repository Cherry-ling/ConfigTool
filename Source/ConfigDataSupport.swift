import Foundation

func configFileSignature(_ url: URL) -> String {
    guard let values = try? url.resourceValues(forKeys: [.contentModificationDateKey, .fileSizeKey]) else {
        return ""
    }
    return "\(values.contentModificationDate?.timeIntervalSince1970 ?? 0)|\(values.fileSize ?? 0)"
}

private indirect enum LuaValue {
    case string(String, Range<Int>)
    case longString(String, Range<Int>)
    case number(String, Range<Int>)
    case bool(Bool, Range<Int>)
    case nilValue(Range<Int>)
    case table([LuaEntry], Range<Int>)
    case raw(String, Range<Int>)

    var range: Range<Int> {
        switch self {
        case .string(_, let range), .longString(_, let range), .number(_, let range),
             .bool(_, let range), .raw(_, let range):
            return range
        case .nilValue(let range), .table(_, let range):
            return range
        }
    }

    var tableEntries: [LuaEntry]? {
        if case .table(let entries, _) = self { return entries }
        return nil
    }

    var keyText: String? {
        switch self {
        case .string(let value, _), .longString(let value, _), .number(let value, _), .raw(let value, _):
            return value
        case .bool(let value, _):
            return value ? "true" : "false"
        default:
            return nil
        }
    }

    var typeName: String {
        switch self {
        case .string, .longString: return "string"
        case .number: return "number"
        case .bool: return "bool"
        case .nilValue: return "nil"
        case .table: return "table"
        case .raw: return "value"
        }
    }
}

private struct LuaEntry {
    let key: LuaValue?
    let implicitIndex: Int
    let value: LuaValue
}

private final class LuaParser {
    private let bytes: [UInt8]
    private(set) var index: Int = 0

    init(data: Data) {
        bytes = Array(data)
    }

    init(text: String) {
        bytes = Array(text.utf8)
    }

    func parseMainTable() throws -> LuaValue {
        guard let start = findBytes(Array("local tmp".utf8)),
              let brace = bytes[start...].firstIndex(of: 123) else {
            throw NSError(domain: "ConfigTool.Lua", code: 1, userInfo: [NSLocalizedDescriptionKey: "未找到 local tmp 配置表"])
        }
        index = brace
        return try parseValue()
    }

    func parseSingleValue() throws -> LuaValue {
        index = 0
        skipTrivia()
        let value = try parseValue()
        skipTrivia()
        guard index == bytes.count else {
            throw NSError(domain: "ConfigTool.Lua", code: 2, userInfo: [NSLocalizedDescriptionKey: "Lua 值后存在无法识别的内容"])
        }
        return value
    }

    private func findBytes(_ needle: [UInt8]) -> Int? {
        guard !needle.isEmpty, bytes.count >= needle.count else { return nil }
        for offset in 0...(bytes.count - needle.count) where Array(bytes[offset..<(offset + needle.count)]) == needle {
            return offset
        }
        return nil
    }

    private func parseValue() throws -> LuaValue {
        skipTrivia()
        guard index < bytes.count else { throw parseError("Lua 值意外结束") }
        let start = index
        switch bytes[index] {
        case 123:
            return try parseTable()
        case 34, 39:
            return try parseQuotedString()
        case 91 where longStringLevel(at: index) != nil:
            return try parseLongString()
        case 45, 48...57:
            return parseNumberOrRaw()
        default:
            let token = parseIdentifier()
            if token == "true" { return .bool(true, start..<index) }
            if token == "false" { return .bool(false, start..<index) }
            if token == "nil" { return .nilValue(start..<index) }
            return .raw(token, start..<index)
        }
    }

    private func parseTable() throws -> LuaValue {
        let start = index
        index += 1
        var entries: [LuaEntry] = []
        var implicitIndex = 1
        while true {
            skipTrivia()
            guard index < bytes.count else { throw parseError("Lua table 缺少 }") }
            if bytes[index] == 125 {
                index += 1
                return .table(entries, start..<index)
            }

            var key: LuaValue?
            var value: LuaValue
            if bytes[index] == 91, longStringLevel(at: index) == nil {
                index += 1
                key = try parseValue()
                skipTrivia()
                try consume(93, message: "Lua table key 缺少 ]")
                skipTrivia()
                try consume(61, message: "Lua table key 缺少 =")
                value = try parseValue()
            } else {
                let saved = index
                if isIdentifierStart(bytes[index]) {
                    let identifier = parseIdentifier()
                    skipTrivia()
                    if index < bytes.count, bytes[index] == 61 {
                        index += 1
                        key = .string(identifier, saved..<(saved + identifier.utf8.count))
                        value = try parseValue()
                    } else {
                        index = saved
                        value = try parseValue()
                    }
                } else {
                    value = try parseValue()
                }
            }
            entries.append(LuaEntry(key: key, implicitIndex: implicitIndex, value: value))
            implicitIndex += 1
            skipTrivia()
            if index < bytes.count, bytes[index] == 44 || bytes[index] == 59 { index += 1 }
        }
    }

    private func parseQuotedString() throws -> LuaValue {
        let start = index
        let quote = bytes[index]
        index += 1
        var output: [UInt8] = []
        while index < bytes.count {
            let byte = bytes[index]
            index += 1
            if byte == quote {
                return .string(String(decoding: output, as: UTF8.self), start..<index)
            }
            if byte == 92, index < bytes.count {
                let escaped = bytes[index]
                index += 1
                switch escaped {
                case 110: output.append(10)
                case 114: output.append(13)
                case 116: output.append(9)
                case 92: output.append(92)
                case 34: output.append(34)
                case 39: output.append(39)
                default: output.append(escaped)
                }
            } else {
                output.append(byte)
            }
        }
        throw parseError("Lua 字符串缺少结束引号")
    }

    private func longStringLevel(at position: Int) -> Int? {
        guard position < bytes.count, bytes[position] == 91 else { return nil }
        var cursor = position + 1
        while cursor < bytes.count, bytes[cursor] == 61 { cursor += 1 }
        return cursor < bytes.count && bytes[cursor] == 91 ? cursor - position - 1 : nil
    }

    private func parseLongString() throws -> LuaValue {
        let start = index
        let level = longStringLevel(at: index) ?? 0
        index += level + 2
        let contentStart = index
        let closing = [UInt8(93)] + Array(repeating: UInt8(61), count: level) + [UInt8(93)]
        while index + closing.count <= bytes.count {
            if Array(bytes[index..<(index + closing.count)]) == closing {
                let value = String(decoding: bytes[contentStart..<index], as: UTF8.self)
                index += closing.count
                return .longString(value, start..<index)
            }
            index += 1
        }
        throw parseError("Lua 长字符串缺少结束标记")
    }

    private func parseNumberOrRaw() -> LuaValue {
        let start = index
        while index < bytes.count,
              ![UInt8(9), 10, 13, 32, 44, 59, 125, 93].contains(bytes[index]) {
            index += 1
        }
        let token = String(decoding: bytes[start..<index], as: UTF8.self)
        if token.range(of: #"^-?(?:\d+\.?\d*|\.\d+)(?:[eE][+-]?\d+)?$"#, options: .regularExpression) != nil {
            return .number(token, start..<index)
        }
        return .raw(token, start..<index)
    }

    private func parseIdentifier() -> String {
        let start = index
        while index < bytes.count, isIdentifierPart(bytes[index]) { index += 1 }
        if start == index {
            while index < bytes.count,
                  ![UInt8(9), 10, 13, 32, 44, 59, 125, 93].contains(bytes[index]) {
                index += 1
            }
        }
        return String(decoding: bytes[start..<index], as: UTF8.self)
    }

    private func skipTrivia() {
        while index < bytes.count {
            if [UInt8(9), 10, 13, 32].contains(bytes[index]) {
                index += 1
                continue
            }
            if index + 1 < bytes.count, bytes[index] == 45, bytes[index + 1] == 45 {
                index += 2
                while index < bytes.count, bytes[index] != 10 { index += 1 }
                continue
            }
            break
        }
    }

    private func consume(_ byte: UInt8, message: String) throws {
        guard index < bytes.count, bytes[index] == byte else { throw parseError(message) }
        index += 1
    }

    private func isIdentifierStart(_ byte: UInt8) -> Bool {
        byte == 95 || (65...90).contains(byte) || (97...122).contains(byte)
    }

    private func isIdentifierPart(_ byte: UInt8) -> Bool {
        isIdentifierStart(byte) || (48...57).contains(byte)
    }

    private func parseError(_ message: String) -> NSError {
        NSError(domain: "ConfigTool.Lua", code: 3, userInfo: [NSLocalizedDescriptionKey: "\(message)（字节 \(index)）"])
    }
}

private enum LuaBindingKind {
    case string
    case longString
    case number
    case bool
    case nilValue
    case table
    case raw
}

private struct LuaBinding {
    let range: Range<Int>
    let kind: LuaBindingKind
}

private struct ParsedLuaConfig {
    let view: WorkbookView
    let bindings: [String: LuaBinding]
}

final class LuaConfigParser {
    func parse(url: URL) throws -> [WorkbookView] {
        [try parseDocument(url: url).view]
    }

    fileprivate func parseDocument(url: URL) throws -> ParsedLuaConfig {
        let data = try Data(contentsOf: url)
        let source = String(decoding: data, as: UTF8.self)
        let root = try LuaParser(data: data).parseMainTable()
        guard let rootEntries = root.tableEntries else {
            throw NSError(domain: "ConfigTool.Lua", code: 10, userInfo: [NSLocalizedDescriptionKey: "tmp 不是 Lua table"])
        }
        let returnedName = returnTableName(in: source)
        let mainEntry = rootEntries.first { $0.key?.keyText == returnedName }
            ?? rootEntries.first { entry in
                guard let name = entry.key?.keyText else { return false }
                return entry.value.tableEntries != nil && !name.hasSuffix("AB") && !name.hasSuffix("Patch")
            }
        guard let selected = mainEntry, let entries = selected.value.tableEntries else {
            throw NSError(domain: "ConfigTool.Lua", code: 11, userInfo: [NSLocalizedDescriptionKey: "未找到 return 对应的 Lua 配置表"])
        }

        let tableName = selected.key?.keyText ?? url.deletingPathExtension().lastPathComponent
        let built = buildMatrix(entries: entries, sourceData: data)
        let attributes = try FileManager.default.attributesOfItem(atPath: url.path)
        let modified = attributes[.modificationDate] as? Date ?? Date.distantPast
        let formatter = ISO8601DateFormatter()
        formatter.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
        let baseName = url.deletingPathExtension().lastPathComponent
        let view = WorkbookView(
            id: "\(url.lastPathComponent)::\(tableName)",
            name: baseName,
            fileName: url.lastPathComponent,
            sheetName: tableName,
            sheetCount: 1,
            category: ConfigCategory.name(for: baseName),
            modifiedAt: formatter.string(from: modified),
            rowCount: built.rows.count,
            columnCount: built.rows.map(\.count).max() ?? 0,
            rows: built.rows,
            sourceKind: "lua",
            sourceSignature: configFileSignature(url),
            editableFromRow: 3,
            lockedCells: built.lockedCells,
            isLoaded: true,
            error: nil
        )
        return ParsedLuaConfig(view: view, bindings: built.bindings)
    }

    private func returnTableName(in source: String) -> String? {
        let pattern = #"return\s+tmp\s*\[\s*[\"']([^\"']+)[\"']\s*\]"#
        guard let regex = try? NSRegularExpression(pattern: pattern),
              let match = regex.firstMatch(in: source, range: NSRange(source.startIndex..., in: source)),
              let range = Range(match.range(at: 1), in: source) else { return nil }
        return String(source[range])
    }

    private func buildMatrix(entries: [LuaEntry], sourceData: Data) -> (rows: [[String]], bindings: [String: LuaBinding], lockedCells: [String]) {
        let recordEntries = entries.filter { $0.value.tableEntries != nil }
        if !recordEntries.isEmpty {
            var columns: [String] = []
            for entry in recordEntries {
                for field in entry.value.tableEntries ?? [] {
                    guard let name = field.key?.keyText, !columns.contains(name) else { continue }
                    columns.append(name)
                }
            }
            var rows: [[String]] = [
                ["键"] + columns,
                ["key"] + columns,
                ["Lua"] + columns.map { _ in "" }
            ]
            var bindings: [String: LuaBinding] = [:]
            var locked: [String] = []
            for entry in entries {
                let rowIndex = rows.count
                let keyValue = entry.key?.keyText ?? String(entry.implicitIndex)
                var row = [keyValue]
                if let key = entry.key {
                    bindings["\(rowIndex):0"] = binding(for: key)
                } else {
                    locked.append("\(rowIndex):0")
                }
                let fields = entry.value.tableEntries ?? []
                for (columnOffset, column) in columns.enumerated() {
                    if let field = fields.first(where: { $0.key?.keyText == column }) {
                        row.append(display(field.value, sourceData: sourceData))
                        bindings["\(rowIndex):\(columnOffset + 1)"] = binding(for: field.value)
                    } else {
                        row.append("")
                        locked.append("\(rowIndex):\(columnOffset + 1)")
                    }
                }
                rows.append(row)
            }
            return (rows, bindings, locked)
        }

        var rows = [["键", "值"], ["key", "value"], ["Lua", ""]]
        var bindings: [String: LuaBinding] = [:]
        var locked: [String] = []
        for entry in entries {
            let rowIndex = rows.count
            rows.append([
                entry.key?.keyText ?? String(entry.implicitIndex),
                display(entry.value, sourceData: sourceData)
            ])
            if let key = entry.key {
                bindings["\(rowIndex):0"] = binding(for: key)
            } else {
                locked.append("\(rowIndex):0")
            }
            bindings["\(rowIndex):1"] = binding(for: entry.value)
        }
        return (rows, bindings, locked)
    }

    private func display(_ value: LuaValue, sourceData: Data) -> String {
        switch value {
        case .string(let value, _), .longString(let value, _), .number(let value, _), .raw(let value, _):
            return value
        case .bool(let value, _):
            return value ? "true" : "false"
        case .nilValue:
            return "nil"
        case .table(_, let range):
            return String(decoding: sourceData[range], as: UTF8.self)
        }
    }

    private func binding(for value: LuaValue) -> LuaBinding {
        switch value {
        case .string(_, let range): return LuaBinding(range: range, kind: .string)
        case .longString(_, let range): return LuaBinding(range: range, kind: .longString)
        case .number(_, let range): return LuaBinding(range: range, kind: .number)
        case .bool(_, let range): return LuaBinding(range: range, kind: .bool)
        case .nilValue(let range): return LuaBinding(range: range, kind: .nilValue)
        case .table(_, let range): return LuaBinding(range: range, kind: .table)
        case .raw(_, let range): return LuaBinding(range: range, kind: .raw)
        }
    }
}

enum ConfigSaveError: LocalizedError {
    case sourceChanged
    case unsupported(String)
    case invalidValue(String)

    var errorDescription: String? {
        switch self {
        case .sourceChanged:
            return "源文件已被外部修改。为避免覆盖新内容，请先刷新后重新编辑。"
        case .unsupported(let message), .invalidValue(let message):
            return message
        }
    }
}

final class ConfigFileSaver {
    func save(directory: URL, id: String, expectedSignature: String, changes: [CellChange]) throws {
        guard let separator = id.range(of: "::") else { throw ConfigSaveError.unsupported("配置标识无效") }
        let fileName = String(id[..<separator.lowerBound])
        let subName = String(id[separator.upperBound...])
        let url = directory.appendingPathComponent(fileName)
        guard configFileSignature(url) == expectedSignature else { throw ConfigSaveError.sourceChanged }
        switch url.pathExtension.lowercased() {
        case "lua":
            try saveLua(url: url, changes: changes)
        case "xlsx":
            try saveXlsx(url: url, sheetName: subName, changes: changes)
        default:
            throw ConfigSaveError.unsupported("暂不支持保存 \(url.pathExtension) 文件")
        }
    }

    private func saveLua(url: URL, changes: [CellChange]) throws {
        let parsed = try LuaConfigParser().parseDocument(url: url)
        var replacements: [(Range<Int>, [UInt8])] = []
        for change in changes {
            let key = "\(change.row):\(change.column)"
            guard let binding = parsed.bindings[key] else {
                throw ConfigSaveError.invalidValue("该 Lua 单元格由结构生成，暂不能新增字段。")
            }
            replacements.append((binding.range, try encodeLua(change.value, kind: binding.kind)))
        }
        var bytes = Array(try Data(contentsOf: url))
        for replacement in replacements.sorted(by: { $0.0.lowerBound > $1.0.lowerBound }) {
            bytes.replaceSubrange(replacement.0, with: replacement.1)
        }
        let result = Data(bytes)
        _ = try LuaParser(data: result).parseMainTable()
        try result.write(to: url, options: .atomic)
    }

    private func encodeLua(_ value: String, kind: LuaBindingKind) throws -> [UInt8] {
        switch kind {
        case .string:
            let escaped = value
                .replacingOccurrences(of: "\\", with: "\\\\")
                .replacingOccurrences(of: "\"", with: "\\\"")
                .replacingOccurrences(of: "\n", with: "\\n")
                .replacingOccurrences(of: "\r", with: "\\r")
                .replacingOccurrences(of: "\t", with: "\\t")
            return Array("\"\(escaped)\"".utf8)
        case .longString:
            var level = 0
            while value.contains("]" + String(repeating: "=", count: level) + "]") { level += 1 }
            let equals = String(repeating: "=", count: level)
            return Array("[\(equals)[\(value)]\(equals)]".utf8)
        case .number:
            guard value.range(of: #"^-?(?:\d+\.?\d*|\.\d+)(?:[eE][+-]?\d+)?$"#, options: .regularExpression) != nil else {
                throw ConfigSaveError.invalidValue("数字字段只能保存合法数字。")
            }
            return Array(value.utf8)
        case .bool:
            guard value == "true" || value == "false" else {
                throw ConfigSaveError.invalidValue("布尔字段只能填写 true 或 false。")
            }
            return Array(value.utf8)
        case .nilValue:
            guard value == "nil" else { throw ConfigSaveError.invalidValue("nil 字段只能保持 nil。") }
            return Array(value.utf8)
        case .table:
            _ = try LuaParser(text: value).parseSingleValue()
            return Array(value.utf8)
        case .raw:
            guard !value.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else {
                throw ConfigSaveError.invalidValue("Lua 值不能为空。")
            }
            return Array(value.utf8)
        }
    }

    private func saveXlsx(url: URL, sheetName: String, changes: [CellChange]) throws {
        let extracted = FileManager.default.temporaryDirectory
            .appendingPathComponent("ConfigToolSave-\(UUID().uuidString)", isDirectory: true)
        try FileManager.default.createDirectory(at: extracted, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: extracted) }
        try run("/usr/bin/unzip", ["-qq", "-o", url.path, "-d", extracted.path])

        let workbookURL = extracted.appendingPathComponent("xl/workbook.xml")
        let workbook = try XMLDocument(contentsOf: workbookURL)
        let sheetNodes = try workbook.nodes(forXPath: "//*[local-name()='sheet']")
        guard let sheet = sheetNodes.compactMap({ $0 as? XMLElement }).first(where: {
            $0.attribute(forName: "name")?.stringValue == sheetName
        }) else { throw ConfigSaveError.unsupported("找不到 Excel Sheet：\(sheetName)") }
        let relationshipId = sheet.attributes?.first(where: { $0.name == "r:id" || $0.localName == "id" })?.stringValue

        let relsURL = extracted.appendingPathComponent("xl/_rels/workbook.xml.rels")
        let rels = try XMLDocument(contentsOf: relsURL)
        let relationshipNodes = try rels.nodes(forXPath: "//*[local-name()='Relationship']")
        guard let target = relationshipNodes.compactMap({ $0 as? XMLElement }).first(where: {
            $0.attribute(forName: "Id")?.stringValue == relationshipId
        })?.attribute(forName: "Target")?.stringValue else {
            throw ConfigSaveError.unsupported("无法定位 Excel Sheet 文件")
        }
        let normalized = target.hasPrefix("/") ? String(target.dropFirst()) : "xl/" + target.replacingOccurrences(of: "../", with: "")
        let worksheetURL = extracted.appendingPathComponent(normalized)
        let worksheet = try XMLDocument(contentsOf: worksheetURL)
        guard let sheetData = try worksheet.nodes(forXPath: "//*[local-name()='sheetData']").first as? XMLElement else {
            throw ConfigSaveError.unsupported("Excel Sheet 缺少 sheetData")
        }

        var cellMap: [String: XMLElement] = [:]
        for node in try worksheet.nodes(forXPath: "//*[local-name()='c']") {
            guard let cell = node as? XMLElement, let reference = cell.attribute(forName: "r")?.stringValue else { continue }
            cellMap[reference] = cell
        }
        for change in changes {
            let reference = "\(columnLetters(change.column + 1))\(change.row + 1)"
            let cell = cellMap[reference] ?? makeCell(reference: reference, row: change.row + 1, sheetData: sheetData)
            setCell(cell, value: change.value)
        }
        try worksheet.xmlData(options: []).write(to: worksheetURL, options: .atomic)

        let output = FileManager.default.temporaryDirectory.appendingPathComponent("ConfigTool-\(UUID().uuidString).xlsx")
        defer { try? FileManager.default.removeItem(at: output) }
        try run("/usr/bin/zip", ["-qr", output.path, "."], currentDirectory: extracted)
        try run("/usr/bin/unzip", ["-tq", output.path])
        try Data(contentsOf: output).write(to: url, options: .atomic)
    }

    private func makeCell(reference: String, row: Int, sheetData: XMLElement) -> XMLElement {
        let rows = (sheetData.children ?? []).compactMap { $0 as? XMLElement }.filter { $0.localName == "row" }
        let rowElement: XMLElement
        if let existing = rows.first(where: { Int($0.attribute(forName: "r")?.stringValue ?? "") == row }) {
            rowElement = existing
        } else {
            rowElement = XMLElement(name: "row")
            rowElement.addAttribute(XMLNode.attribute(withName: "r", stringValue: String(row)) as! XMLNode)
            sheetData.addChild(rowElement)
        }
        let cell = XMLElement(name: "c")
        cell.addAttribute(XMLNode.attribute(withName: "r", stringValue: reference) as! XMLNode)
        rowElement.addChild(cell)
        return cell
    }

    private func setCell(_ cell: XMLElement, value: String) {
        while let child = cell.children?.first { child.detach() }
        cell.removeAttribute(forName: "t")
        if value.hasPrefix("=") {
            let formula = XMLElement(name: "f", stringValue: String(value.dropFirst()))
            cell.addChild(formula)
        } else if value.range(of: #"^-?(?:0|[1-9]\d*)(?:\.\d+)?$"#, options: .regularExpression) != nil {
            let node = XMLElement(name: "v", stringValue: value)
            cell.addChild(node)
        } else {
            cell.addAttribute(XMLNode.attribute(withName: "t", stringValue: "inlineStr") as! XMLNode)
            let inline = XMLElement(name: "is")
            let text = XMLElement(name: "t", stringValue: value)
            if value.hasPrefix(" ") || value.hasSuffix(" ") {
                text.addAttribute(XMLNode.attribute(withName: "xml:space", stringValue: "preserve") as! XMLNode)
            }
            inline.addChild(text)
            cell.addChild(inline)
        }
    }

    private func columnLetters(_ column: Int) -> String {
        var value = column
        var output = ""
        while value > 0 {
            value -= 1
            output = String(UnicodeScalar(65 + value % 26)!) + output
            value /= 26
        }
        return output
    }

    private func run(_ executable: String, _ arguments: [String], currentDirectory: URL? = nil) throws {
        let process = Process()
        let errors = Pipe()
        process.executableURL = URL(fileURLWithPath: executable)
        process.arguments = arguments
        process.currentDirectoryURL = currentDirectory
        process.standardError = errors
        try process.run()
        process.waitUntilExit()
        guard process.terminationStatus == 0 else {
            let message = String(data: errors.fileHandleForReading.readDataToEndOfFile(), encoding: .utf8) ?? ""
            throw ConfigSaveError.unsupported(message.isEmpty ? "文件写入工具执行失败" : message)
        }
    }
}
