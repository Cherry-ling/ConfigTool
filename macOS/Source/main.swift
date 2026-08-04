import AppKit
import Foundation
import UniformTypeIdentifiers
import WebKit

private let defaultConfigPath = "/Users/lingkunwang/Desktop/twoblocks-frontend/BlockColorMatch/configExcel"

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

struct DirectoryPayload: Codable {
    let directory: String
    let scannedAt: String
    let fileCount: Int
    let workbooks: [WorkbookView]
}

struct CellChange: Codable {
    let row: Int
    let column: Int
    let value: String
}

struct ReverseReference: Codable {
    let bookId: String
    let bookLabel: String
    let field: String
    let row: Int
    let column: Int
    let cellValue: String
    let rowName: String?
    let matchMode: String
}

struct ReverseReferenceResponse: Codable {
    let requestId: Int
    let value: String
    let references: [ReverseReference]
}

private struct RelationRule {
    let sources: [String]
    let fields: [String]
    let mode: String
    let tupleIndex: Int

    init(dictionary: [String: Any]) {
        sources = dictionary["sources"] as? [String] ?? []
        fields = dictionary["fields"] as? [String] ?? []
        mode = dictionary["mode"] as? String ?? "scalar"
        tupleIndex = dictionary["tupleIndex"] as? Int ?? (dictionary["tupleIndex"] as? NSNumber)?.intValue ?? 0
    }
}

struct GlobalSearchMatch: Codable {
    let bookId: String
    let bookLabel: String
    let field: String
    let row: Int
    let column: Int
    let value: String
    let rowPreview: String
}

struct GlobalSearchResponse: Codable {
    let requestId: Int
    let query: String
    let totalCount: Int
    let matches: [GlobalSearchMatch]
}

private struct WorkbookSheet {
    let name: String
    let relationshipId: String
}

private struct CellRecord {
    let row: Int
    let column: Int
    let type: String?
    let style: Int?
    let value: String
}

enum ConfigCategory {
    static func name(for table: String) -> String {
        let lower = table.lowercased()
        if lower.hasPrefix("activity") || ["battlepass", "weeklyrank", "bot"].contains(lower) {
            return "活动"
        }
        if lower.contains("level") || lower.contains("creator") || lower.contains("feature") {
            return "关卡"
        }
        if lower.contains("reward") || lower.contains("shop") || lower.contains("payment") || lower.contains("chest") {
            return "奖励与商业化"
        }
        if lower.contains("item") || lower.contains("profile") || lower.contains("avatar") || lower.contains("badge") {
            return "物品与角色"
        }
        return "基础配置"
    }
}

private final class WorkbookParser {
    private let isoFormatter: ISO8601DateFormatter = {
        let formatter = ISO8601DateFormatter()
        formatter.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
        return formatter
    }()

    func parse(url: URL) throws -> [WorkbookView] {
        let attributes = try FileManager.default.attributesOfItem(atPath: url.path)
        let modified = attributes[.modificationDate] as? Date ?? Date.distantPast
        let baseName = url.deletingPathExtension().lastPathComponent
        let extractedDirectory = try extractArchive(url)
        defer { try? FileManager.default.removeItem(at: extractedDirectory) }

        let sharedStrings = try parseSharedStrings(xml: readEntry("xl/sharedStrings.xml", from: extractedDirectory, allowMissing: true))
        let workbookXML = try readEntry("xl/workbook.xml", from: extractedDirectory)
        let sheets = try parseWorkbook(xml: workbookXML)
        guard !sheets.isEmpty else {
            throw NSError(domain: "ConfigTool", code: 11, userInfo: [NSLocalizedDescriptionKey: "工作簿没有可读取的 Sheet"])
        }

        let relationshipXML = try readEntry("xl/_rels/workbook.xml.rels", from: extractedDirectory)
        let relationships = try parseRelationships(xml: relationshipXML)
        let dateStyles = try parseDateStyleIndexes(xml: readEntry("xl/styles.xml", from: extractedDirectory, allowMissing: true))
        var views: [WorkbookView] = []
        for sheet in sheets {
            guard var target = relationships[sheet.relationshipId] else {
                throw NSError(domain: "ConfigTool", code: 12, userInfo: [NSLocalizedDescriptionKey: "无法定位 Sheet：\(sheet.name)"])
            }
            if target.hasPrefix("/") {
                target.removeFirst()
            } else if !target.hasPrefix("xl/") {
                target = "xl/" + target.replacingOccurrences(of: "../", with: "")
            }

            let cells = try parseWorksheet(xml: readEntry(target, from: extractedDirectory))
            let matrix = makeMatrix(cells: cells, sharedStrings: sharedStrings, dateStyles: dateStyles)
            views.append(WorkbookView(
                id: "\(url.lastPathComponent)::\(sheet.name)",
                name: baseName,
                fileName: url.lastPathComponent,
                sheetName: sheet.name,
                sheetCount: sheets.count,
                category: ConfigCategory.name(for: baseName),
                modifiedAt: isoFormatter.string(from: modified),
                rowCount: matrix.count,
                columnCount: matrix.map(\.count).max() ?? 0,
                rows: matrix,
                sourceKind: "xlsx",
                sourceSignature: configFileSignature(url),
                editableFromRow: 0,
                lockedCells: [],
                isLoaded: true,
                error: nil
            ))
        }
        return views
    }

    private func extractArchive(_ workbook: URL) throws -> URL {
        let directory = FileManager.default.temporaryDirectory
            .appendingPathComponent("PairPairConfigTool", isDirectory: true)
            .appendingPathComponent(UUID().uuidString, isDirectory: true)
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        let process = Process()
        let errors = Pipe()
        process.executableURL = URL(fileURLWithPath: "/usr/bin/unzip")
        process.arguments = ["-qq", "-o", workbook.path, "-d", directory.path]
        process.standardOutput = FileHandle.nullDevice
        process.standardError = errors
        try process.run()
        process.waitUntilExit()
        if process.terminationStatus != 0 {
            let message = String(data: errors.fileHandleForReading.readDataToEndOfFile(), encoding: .utf8) ?? "unzip 失败"
            try? FileManager.default.removeItem(at: directory)
            throw NSError(domain: "ConfigTool", code: Int(process.terminationStatus), userInfo: [NSLocalizedDescriptionKey: message])
        }
        return directory
    }

    private func readEntry(_ entry: String, from directory: URL, allowMissing: Bool = false) throws -> Data {
        let url = directory.appendingPathComponent(entry)
        if allowMissing && !FileManager.default.fileExists(atPath: url.path) {
            return Data()
        }
        return try Data(contentsOf: url)
    }

    private func parseSharedStrings(xml: Data) throws -> [String] {
        guard !xml.isEmpty else { return [] }
        let delegate = SharedStringsDelegate()
        let parser = XMLParser(data: xml)
        parser.delegate = delegate
        guard parser.parse() else {
            throw parser.parserError ?? NSError(domain: "ConfigTool", code: 20, userInfo: [NSLocalizedDescriptionKey: "sharedStrings.xml 解析失败"])
        }
        return delegate.strings
    }

    private func parseWorkbook(xml: Data) throws -> [WorkbookSheet] {
        let delegate = WorkbookDelegate()
        let parser = XMLParser(data: xml)
        parser.delegate = delegate
        guard parser.parse() else {
            throw parser.parserError ?? NSError(domain: "ConfigTool", code: 21, userInfo: [NSLocalizedDescriptionKey: "workbook.xml 解析失败"])
        }
        return delegate.sheets
    }

    private func parseRelationships(xml: Data) throws -> [String: String] {
        let delegate = RelationshipsDelegate()
        let parser = XMLParser(data: xml)
        parser.delegate = delegate
        guard parser.parse() else {
            throw parser.parserError ?? NSError(domain: "ConfigTool", code: 22, userInfo: [NSLocalizedDescriptionKey: "workbook relationships 解析失败"])
        }
        return delegate.relationships
    }

    private func parseWorksheet(xml: Data) throws -> [CellRecord] {
        let delegate = WorksheetDelegate()
        let parser = XMLParser(data: xml)
        parser.delegate = delegate
        guard parser.parse() else {
            throw parser.parserError ?? NSError(domain: "ConfigTool", code: 23, userInfo: [NSLocalizedDescriptionKey: "Sheet XML 解析失败"])
        }
        return delegate.cells
    }

    private func parseDateStyleIndexes(xml: Data) throws -> Set<Int> {
        guard !xml.isEmpty else { return [] }
        let delegate = StylesDelegate()
        let parser = XMLParser(data: xml)
        parser.delegate = delegate
        guard parser.parse() else { return [] }
        return delegate.dateStyleIndexes
    }

    private func makeMatrix(cells: [CellRecord], sharedStrings: [String], dateStyles: Set<Int>) -> [[String]] {
        guard let maxRow = cells.map(\.row).max(), let maxColumn = cells.map(\.column).max() else {
            return []
        }
        var matrix = Array(repeating: Array(repeating: "", count: maxColumn + 1), count: maxRow + 1)
        for cell in cells {
            var display = cell.value
            if cell.type == "s", let index = Int(cell.value), sharedStrings.indices.contains(index) {
                display = sharedStrings[index]
            } else if cell.type == "b" {
                display = cell.value == "1" ? "true" : "false"
            } else if let style = cell.style, dateStyles.contains(style), let serial = Double(cell.value) {
                display = formatExcelDate(serial)
            }
            matrix[cell.row][cell.column] = display
        }

        while matrix.last?.allSatisfy({ $0.isEmpty }) == true {
            matrix.removeLast()
        }
        let actualColumnCount = matrix.map { row in
            (row.lastIndex(where: { !$0.isEmpty }) ?? -1) + 1
        }.max() ?? 0
        return matrix.map { Array($0.prefix(actualColumnCount)) }
    }

    private func formatExcelDate(_ serial: Double) -> String {
        let secondsPerDay = 86_400.0
        let reference = Date(timeIntervalSince1970: -2_209_161_600)
        let date = reference.addingTimeInterval(serial * secondsPerDay)
        let formatter = DateFormatter()
        formatter.locale = Locale(identifier: "zh_CN")
        formatter.timeZone = TimeZone(secondsFromGMT: 0)
        formatter.dateFormat = serial.rounded(.towardZero) == serial ? "yyyy-MM-dd" : "yyyy-MM-dd HH:mm:ss"
        return formatter.string(from: date)
    }
}

private final class SharedStringsDelegate: NSObject, XMLParserDelegate {
    var strings: [String] = []
    private var insideItem = false
    private var insideText = false
    private var buffer = ""

    func parser(_ parser: XMLParser, didStartElement elementName: String, namespaceURI: String?, qualifiedName qName: String?, attributes attributeDict: [String: String] = [:]) {
        if elementName == "si" {
            insideItem = true
            buffer = ""
        } else if insideItem && elementName == "t" {
            insideText = true
        }
    }

    func parser(_ parser: XMLParser, foundCharacters string: String) {
        if insideText { buffer += string }
    }

    func parser(_ parser: XMLParser, didEndElement elementName: String, namespaceURI: String?, qualifiedName qName: String?) {
        if elementName == "t" {
            insideText = false
        } else if elementName == "si" {
            strings.append(buffer)
            insideItem = false
        }
    }
}

private final class WorkbookDelegate: NSObject, XMLParserDelegate {
    var sheets: [WorkbookSheet] = []

    func parser(_ parser: XMLParser, didStartElement elementName: String, namespaceURI: String?, qualifiedName qName: String?, attributes attributeDict: [String: String] = [:]) {
        guard elementName == "sheet",
              attributeDict["state"] != "hidden",
              let name = attributeDict["name"],
              let relation = attributeDict["r:id"] ?? attributeDict["id"] else { return }
        sheets.append(WorkbookSheet(name: name, relationshipId: relation))
    }
}

private final class RelationshipsDelegate: NSObject, XMLParserDelegate {
    var relationships: [String: String] = [:]

    func parser(_ parser: XMLParser, didStartElement elementName: String, namespaceURI: String?, qualifiedName qName: String?, attributes attributeDict: [String: String] = [:]) {
        guard elementName == "Relationship",
              let id = attributeDict["Id"],
              let target = attributeDict["Target"],
              (attributeDict["Type"] ?? "").contains("/worksheet") else { return }
        relationships[id] = target
    }
}

private final class WorksheetDelegate: NSObject, XMLParserDelegate {
    var cells: [CellRecord] = []
    private var reference = ""
    private var type: String?
    private var style: Int?
    private var value = ""
    private var inlineValue = ""
    private var insideValue = false
    private var insideInlineText = false

    func parser(_ parser: XMLParser, didStartElement elementName: String, namespaceURI: String?, qualifiedName qName: String?, attributes attributeDict: [String: String] = [:]) {
        if elementName == "c" {
            reference = attributeDict["r"] ?? ""
            type = attributeDict["t"]
            style = attributeDict["s"].flatMap(Int.init)
            value = ""
            inlineValue = ""
        } else if elementName == "v" {
            insideValue = true
        } else if elementName == "t" && type == "inlineStr" {
            insideInlineText = true
        }
    }

    func parser(_ parser: XMLParser, foundCharacters string: String) {
        if insideValue {
            value += string
        } else if insideInlineText {
            inlineValue += string
        }
    }

    func parser(_ parser: XMLParser, didEndElement elementName: String, namespaceURI: String?, qualifiedName qName: String?) {
        if elementName == "v" {
            insideValue = false
        } else if elementName == "t" {
            insideInlineText = false
        } else if elementName == "c", let coordinate = Self.coordinate(reference) {
            cells.append(CellRecord(
                row: coordinate.row,
                column: coordinate.column,
                type: type,
                style: style,
                value: type == "inlineStr" ? inlineValue : value
            ))
        }
    }

    private static func coordinate(_ reference: String) -> (row: Int, column: Int)? {
        var column = 0
        var rowText = ""
        for scalar in reference.unicodeScalars {
            if scalar.value >= 65 && scalar.value <= 90 {
                column = column * 26 + Int(scalar.value - 64)
            } else if scalar.value >= 48 && scalar.value <= 57 {
                rowText.unicodeScalars.append(scalar)
            }
        }
        guard let row = Int(rowText), column > 0, row > 0 else { return nil }
        return (row - 1, column - 1)
    }
}

private final class StylesDelegate: NSObject, XMLParserDelegate {
    var dateStyleIndexes: Set<Int> = []
    private var customDateFormats: Set<Int> = []
    private var insideCellXfs = false
    private var styleIndex = 0

    func parser(_ parser: XMLParser, didStartElement elementName: String, namespaceURI: String?, qualifiedName qName: String?, attributes attributeDict: [String: String] = [:]) {
        if elementName == "numFmt",
           let id = attributeDict["numFmtId"].flatMap(Int.init),
           let code = attributeDict["formatCode"]?.lowercased(),
           Self.looksLikeDateFormat(code) {
            customDateFormats.insert(id)
        } else if elementName == "cellXfs" {
            insideCellXfs = true
            styleIndex = 0
        } else if elementName == "xf", insideCellXfs {
            let formatId = attributeDict["numFmtId"].flatMap(Int.init) ?? 0
            if Self.builtInDateFormats.contains(formatId) || customDateFormats.contains(formatId) {
                dateStyleIndexes.insert(styleIndex)
            }
            styleIndex += 1
        }
    }

    func parser(_ parser: XMLParser, didEndElement elementName: String, namespaceURI: String?, qualifiedName qName: String?) {
        if elementName == "cellXfs" { insideCellXfs = false }
    }

    private static let builtInDateFormats = Set(14...22).union(Set(45...47))

    private static func looksLikeDateFormat(_ code: String) -> Bool {
        let stripped = code.replacingOccurrences(of: #""[^"]*""#, with: "", options: .regularExpression)
        return stripped.contains("yy") || stripped.contains("dd") || stripped.contains("hh") || stripped.contains("ss")
    }
}

private final class DirectoryLoader {
    private let workbookParser = WorkbookParser()
    private let luaParser = LuaConfigParser()
    private var cache: [String: (signature: String, workbooks: [WorkbookView])] = [:]

    func signature(for directory: URL) -> String {
        guard let urls = try? excelURLs(in: directory) else { return "" }
        return urls.compactMap { url in
            guard let values = try? url.resourceValues(forKeys: [.contentModificationDateKey, .fileSizeKey]) else { return nil }
            return "\(url.lastPathComponent)|\(values.contentModificationDate?.timeIntervalSince1970 ?? 0)|\(values.fileSize ?? 0)"
        }.joined(separator: "\n")
    }

    func load(directory: URL, includeRows: Bool = false) throws -> DirectoryPayload {
        let urls = try excelURLs(in: directory)
        var workbooks: [WorkbookView] = []
        for url in urls {
            let values = try url.resourceValues(forKeys: [.contentModificationDateKey, .fileSizeKey])
            let fileSignature = "\(values.contentModificationDate?.timeIntervalSince1970 ?? 0)|\(values.fileSize ?? 0)"
            if let cached = cache[url.path], cached.signature == fileSignature {
                workbooks.append(contentsOf: includeRows ? cached.workbooks : cached.workbooks.map(summary))
                continue
            }
            do {
                let parsedSheets: [WorkbookView]
                if url.pathExtension.lowercased() == "lua" {
                    parsedSheets = try luaParser.parse(url: url)
                } else {
                    parsedSheets = try workbookParser.parse(url: url)
                }
                cache[url.path] = (fileSignature, parsedSheets)
                workbooks.append(contentsOf: includeRows ? parsedSheets : parsedSheets.map(summary))
            } catch {
                let fallback = WorkbookView(
                    id: "\(url.lastPathComponent)::error",
                    name: url.deletingPathExtension().lastPathComponent,
                    fileName: url.lastPathComponent,
                    sheetName: "",
                    sheetCount: 0,
                    category: ConfigCategory.name(for: url.deletingPathExtension().lastPathComponent),
                    modifiedAt: ISO8601DateFormatter().string(from: values.contentModificationDate ?? Date.distantPast),
                    rowCount: 0,
                    columnCount: 0,
                    rows: [],
                    sourceKind: url.pathExtension.lowercased(),
                    sourceSignature: configFileSignature(url),
                    editableFromRow: 0,
                    lockedCells: [],
                    isLoaded: true,
                    error: error.localizedDescription
                )
                workbooks.append(fallback)
            }
        }
        return DirectoryPayload(
            directory: directory.path,
            scannedAt: ISO8601DateFormatter().string(from: Date()),
            fileCount: urls.count,
            workbooks: workbooks
        )
    }

    func loadWorkbook(directory: URL, id: String) throws -> WorkbookView {
        guard let fileName = id.components(separatedBy: "::").first, !fileName.isEmpty else {
            throw NSError(domain: "ConfigTool", code: 31, userInfo: [NSLocalizedDescriptionKey: "配置标识无效"])
        }
        let url = directory.appendingPathComponent(fileName)
        let signature = configFileSignature(url)
        let parsed: [WorkbookView]
        if let cached = cache[url.path], cached.signature == signature {
            parsed = cached.workbooks
        } else if url.pathExtension.lowercased() == "lua" {
            parsed = try luaParser.parse(url: url)
            cache[url.path] = (signature, parsed)
        } else {
            parsed = try workbookParser.parse(url: url)
            cache[url.path] = (signature, parsed)
        }
        guard let workbook = parsed.first(where: { $0.id == id }) else {
            throw NSError(domain: "ConfigTool", code: 32, userInfo: [NSLocalizedDescriptionKey: "找不到配置页：\(id)"])
        }
        return workbook
    }

    func findReverseReferences(
        directory: URL,
        value: String,
        targetTokens: [String],
        scalarFields: [String],
        jsonFields: [String],
        relationRules: [RelationRule] = []
    ) throws -> [ReverseReference] {
        let payload = try load(directory: directory, includeRows: true)
        let wantedTokens = Set(targetTokens.map(Self.normalizeRelationToken).filter { !$0.isEmpty })
        let scalarFieldSet = Set(scalarFields.map(Self.fieldName))
        let jsonFieldSet = Set(jsonFields.map(Self.fieldName))
        let expected = Self.comparableRelationValue(value)
        var references: [ReverseReference] = []

        for workbook in payload.workbooks where workbook.error == nil && workbook.rows.count > 3 {
            let headers = Self.fieldHeaders(for: workbook)
            let sourceTokens = Self.workbookRelationTokens(workbook)
            let sourcePrimaryToken = Self.workbookPrimaryRelationToken(workbook)
            let nameColumn = headers.firstIndex { Self.fieldName($0) == "name" }

            for column in 0..<workbook.columnCount {
                guard column < headers.count else { continue }
                let field = headers[column].trimmingCharacters(in: .whitespacesAndNewlines)
                guard !field.isEmpty else { continue }
                let lowerField = Self.fieldName(field)
                var mode: String?
                var tupleIndex = 0

                if let rule = relationRules.first(where: {
                    Self.ruleApplies($0, sourceTokens: sourceTokens) && $0.fields.contains(where: { Self.fieldName($0) == lowerField })
                }) {
                    mode = rule.mode
                    tupleIndex = rule.tupleIndex
                } else if jsonFieldSet.contains(lowerField) {
                    mode = "jsonKeys"
                } else if scalarFieldSet.contains(lowerField) {
                    mode = "scalar"
                } else if let inferred = Self.inferredRelationTarget(field),
                          wantedTokens.contains(Self.normalizeRelationToken(inferred)) {
                    mode = "scalar"
                } else if Self.isIdentifierField(field),
                          wantedTokens.contains(where: { target in
                              target.count >= 4 &&
                              sourcePrimaryToken.hasPrefix(target) &&
                              sourcePrimaryToken.count > target.count
                          }) {
                    mode = "scalar"
                } else if sourceTokens.contains("activity"), lowerField == "subid" {
                    mode = "activitySubId"
                }

                guard let mode else { continue }
                for row in 3..<workbook.rows.count {
                    guard column < workbook.rows[row].count else { continue }
                    let cellValue = workbook.rows[row][column]
                    let rowName = nameColumn.flatMap { index in
                        index < workbook.rows[row].count ? workbook.rows[row][index] : nil
                    }
                    let matches: Bool
                    switch mode {
                    case "jsonKeys":
                        matches = Self.jsonKeys(in: cellValue).contains {
                            Self.comparableRelationValue($0) == expected
                        }
                    case "activitySubId":
                        matches =
                            wantedTokens.contains(Self.normalizeRelationToken(rowName ?? "")) &&
                            Self.comparableRelationValue(cellValue) == expected
                    case "list":
                        matches = Self.structuredRelationValues(in: cellValue, tupleIndex: nil).contains {
                            Self.comparableRelationValue($0) == expected
                        }
                    case "tuple":
                        matches = Self.structuredRelationValues(in: cellValue, tupleIndex: tupleIndex).contains {
                            Self.comparableRelationValue($0) == expected
                        }
                    default:
                        matches = Self.comparableRelationValue(cellValue) == expected
                    }
                    guard matches else { continue }
                    references.append(ReverseReference(
                        bookId: workbook.id,
                        bookLabel: workbook.sheetCount > 1
                            ? "\(workbook.name) · \(workbook.sheetName)"
                            : workbook.name,
                        field: field,
                        row: row,
                        column: column,
                        cellValue: cellValue,
                        rowName: rowName,
                        matchMode: mode
                    ))
                }
            }
        }
        return references
    }

    func findGlobalMatches(
        directory: URL,
        query: String,
        limit: Int = 500
    ) throws -> (matches: [GlobalSearchMatch], totalCount: Int) {
        let keyword = query.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !keyword.isEmpty else { return ([], 0) }
        let payload = try load(directory: directory, includeRows: true)
        var matches: [GlobalSearchMatch] = []
        var totalCount = 0

        for workbook in payload.workbooks where workbook.error == nil && workbook.rows.count > 3 {
            let headers = Self.fieldHeaders(for: workbook)
            let bookLabel = workbook.sheetCount > 1
                ? "\(workbook.name) · \(workbook.sheetName)"
                : workbook.name
            for row in 3..<workbook.rows.count {
                let values = workbook.rows[row]
                let preview = values
                    .filter { !$0.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty }
                    .prefix(4)
                    .joined(separator: " · ")
                let rowPreview = String(preview.prefix(180))
                for column in 0..<values.count {
                    let value = values[column]
                    guard value.range(
                        of: keyword,
                        options: [.caseInsensitive, .diacriticInsensitive]
                    ) != nil else { continue }
                    totalCount += 1
                    guard matches.count < limit else { continue }
                    let field = column < headers.count && !headers[column].isEmpty
                        ? headers[column]
                        : "第 \(column + 1) 列"
                    matches.append(GlobalSearchMatch(
                        bookId: workbook.id,
                        bookLabel: bookLabel,
                        field: field,
                        row: row,
                        column: column,
                        value: value,
                        rowPreview: rowPreview
                    ))
                }
            }
        }
        return (matches, totalCount)
    }

    private func summary(_ workbook: WorkbookView) -> WorkbookView {
        WorkbookView(
            id: workbook.id,
            name: workbook.name,
            fileName: workbook.fileName,
            sheetName: workbook.sheetName,
            sheetCount: workbook.sheetCount,
            category: workbook.category,
            modifiedAt: workbook.modifiedAt,
            rowCount: workbook.rowCount,
            columnCount: workbook.columnCount,
            rows: [],
            sourceKind: workbook.sourceKind,
            sourceSignature: workbook.sourceSignature,
            editableFromRow: workbook.editableFromRow,
            lockedCells: [],
            isLoaded: false,
            error: workbook.error
        )
    }

    private static func fieldHeaders(for workbook: WorkbookView) -> [String] {
        for headers in workbook.rows.prefix(3) {
            if headers.contains(where: isIdentifierField) {
                return headers
            }
        }
        for index in 1..<min(3, workbook.rows.count) {
            let values = workbook.rows[index].filter { !$0.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty }
            if values.count >= 2, values.filter(looksLikeFieldType).count >= 2 {
                return workbook.rows[index - 1]
            }
        }
        if workbook.rows.count > 1 { return workbook.rows[1] }
        return workbook.rows.first ?? []
    }

    private static func fieldName(_ value: String) -> String {
        value.trimmingCharacters(in: .whitespacesAndNewlines)
            .components(separatedBy: "@").first?
            .trimmingCharacters(in: .whitespacesAndNewlines)
            .lowercased() ?? ""
    }

    private static func isIdentifierField(_ value: String) -> Bool {
        let raw = value.trimmingCharacters(in: .whitespacesAndNewlines)
        let field = fieldName(raw)
        return ["id", "subid"].contains(field) || raw.lowercased().hasSuffix("@id")
    }

    private static func looksLikeFieldType(_ value: String) -> Bool {
        value.range(
            of: #"^(?:repeated\s+)*(?:u?int(?:8|16|32|64)?|u?long|float|double|bool|boolean|string|json|[a-z][a-z0-9_]*enum|e[a-z][a-z0-9_]*)$"#,
            options: [.regularExpression, .caseInsensitive]
        ) != nil
    }

    private func excelURLs(in directory: URL) throws -> [URL] {
        let values: Set<URLResourceKey> = [.isRegularFileKey, .contentModificationDateKey, .fileSizeKey]
        return try FileManager.default.contentsOfDirectory(at: directory, includingPropertiesForKeys: Array(values), options: [.skipsHiddenFiles])
            .filter { url in
                ["xlsx", "lua"].contains(url.pathExtension.lowercased()) &&
                !url.lastPathComponent.hasPrefix("~$")
            }
            .sorted { $0.lastPathComponent.localizedStandardCompare($1.lastPathComponent) == .orderedAscending }
    }

    func invalidate(path: String) {
        cache[path] = nil
    }

    private static func normalizeRelationToken(_ value: String) -> String {
        var token = value
            .replacingOccurrences(of: #"\.(xlsx|lua)$"#, with: "", options: [.regularExpression, .caseInsensitive])
            .components(separatedBy: "@").first ?? ""
        token = token.replacingOccurrences(
            of: #"(config|cfg|table|design|column|server)$"#,
            with: "",
            options: [.regularExpression, .caseInsensitive]
        )
        return token
            .replacingOccurrences(of: #"[^a-zA-Z0-9]"#, with: "", options: .regularExpression)
            .lowercased()
    }

    private static func workbookRelationTokens(_ workbook: WorkbookView) -> Set<String> {
        let fileBase = (workbook.fileName as NSString).deletingPathExtension
        return Set([
            workbook.name,
            fileBase,
            workbook.sheetName,
            workbook.sheetName.components(separatedBy: "@").first ?? ""
        ].map(normalizeRelationToken).filter { !$0.isEmpty })
    }

    private static func workbookPrimaryRelationToken(_ workbook: WorkbookView) -> String {
        let sheetToken = normalizeRelationToken(workbook.sheetName)
        let nameToken = normalizeRelationToken(workbook.name)
        if workbook.sheetCount > 1,
           !sheetToken.isEmpty,
           sheetToken.range(of: #"^sheet\d*$"#, options: [.regularExpression, .caseInsensitive]) == nil {
            return sheetToken
        }
        return nameToken
    }

    private static func inferredRelationTarget(_ field: String) -> String? {
        var target = field.replacingOccurrences(
            of: #"(?:Config|Cfg)?(?:ID|Id)$"#,
            with: "",
            options: [.regularExpression, .caseInsensitive]
        )
        target = target.replacingOccurrences(
            of: #"_id$"#,
            with: "",
            options: [.regularExpression, .caseInsensitive]
        )
        return target == field || target.isEmpty ? nil : target
    }

    private static func comparableRelationValue(_ value: String) -> String {
        let text = value.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !text.isEmpty, let decimal = Decimal(string: text, locale: Locale(identifier: "en_US_POSIX")) else {
            return text
        }
        return NSDecimalNumber(decimal: decimal).stringValue
    }

    private static func jsonKeys(in value: String) -> [String] {
        guard value.contains("{"),
              let expression = try? NSRegularExpression(pattern: #"["']?([a-zA-Z0-9_.:-]+)["']?\s*:"#) else {
            return []
        }
        let range = NSRange(value.startIndex..<value.endIndex, in: value)
        return expression.matches(in: value, range: range).compactMap { match in
            guard let keyRange = Range(match.range(at: 1), in: value) else { return nil }
            return String(value[keyRange])
        }
    }

    private static func ruleApplies(_ rule: RelationRule, sourceTokens: Set<String>) -> Bool {
        rule.sources.isEmpty || rule.sources.contains { sourceTokens.contains(normalizeRelationToken($0)) }
    }

    private static func structuredRelationValues(in value: String, tupleIndex: Int?) -> [String] {
        var values: [String] = []
        if let data = value.data(using: .utf8),
           let object = try? JSONSerialization.jsonObject(with: data) {
            visit(object, tupleIndex: tupleIndex, values: &values)
        } else if let tupleIndex {
            let expression = try? NSRegularExpression(pattern: #"\[[^\[\]]*\]"#)
            let range = NSRange(value.startIndex..<value.endIndex, in: value)
            expression?.matches(in: value, range: range).forEach { match in
                guard let tupleRange = Range(match.range, in: value) else { return }
                let parts = value[tupleRange].dropFirst().dropLast().split(separator: ",", omittingEmptySubsequences: false)
                guard tupleIndex < parts.count else { return }
                values.append(parts[tupleIndex].trimmingCharacters(in: .whitespacesAndNewlines).trimmingCharacters(in: CharacterSet(charactersIn: "\"'")))
            }
        } else if let expression = try? NSRegularExpression(pattern: #"-?\d+(?:\.\d+)?|[a-zA-Z_][\w.-]*"#) {
            let range = NSRange(value.startIndex..<value.endIndex, in: value)
            values = expression.matches(in: value, range: range).compactMap { match in
                guard let valueRange = Range(match.range, in: value) else { return nil }
                return String(value[valueRange])
            }
        }
        return Array(Set(values.filter { !$0.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty }))
    }

    private static func visit(_ object: Any, tupleIndex: Int?, values: inout [String]) {
        guard let array = object as? [Any] else { return }
        let isTuple = !array.isEmpty && array.allSatisfy { !($0 is [Any]) && !($0 is [String: Any]) }
        if isTuple, let tupleIndex, tupleIndex < array.count {
            values.append(primitiveString(array[tupleIndex]))
            return
        }
        for item in array {
            if item is [Any] { visit(item, tupleIndex: tupleIndex, values: &values) }
            else if tupleIndex == nil { values.append(primitiveString(item)) }
        }
    }

    private static func primitiveString(_ value: Any) -> String {
        if let value = value as? String { return value }
        return String(describing: value)
    }
}

private final class DirectoryMonitor {
    private var source: DispatchSourceFileSystemObject?
    private var descriptor: Int32 = -1

    func watch(path: String, onChange: @escaping () -> Void) {
        stop()
        descriptor = open(path, O_EVTONLY)
        guard descriptor >= 0 else { return }
        let source = DispatchSource.makeFileSystemObjectSource(
            fileDescriptor: descriptor,
            eventMask: [.write, .rename, .delete, .extend, .attrib],
            queue: DispatchQueue.global(qos: .utility)
        )
        source.setEventHandler(handler: onChange)
        source.setCancelHandler { [descriptor] in close(descriptor) }
        self.source = source
        source.resume()
    }

    func stop() {
        source?.cancel()
        source = nil
        descriptor = -1
    }

    deinit { stop() }
}

private final class WindowDragView: NSView {
    override func mouseDown(with event: NSEvent) {
        if event.clickCount == 2 {
            window?.zoom(nil)
        } else {
            window?.performDrag(with: event)
        }
    }
}

private final class AppController: NSObject, NSApplicationDelegate, WKScriptMessageHandler, WKUIDelegate {
    private var window: NSWindow!
    private var webView: WKWebView!
    private let loader = DirectoryLoader()
    private let saver = ConfigFileSaver()
    private let gitRepository = GitRepositoryService()
    private let monitor = DirectoryMonitor()
    private let loaderQueue = DispatchQueue(label: "com.pairpair.configtool.loader", qos: .userInitiated)
    private var currentDirectory: URL
    private var currentSignature = ""
    private var reloadWorkItem: DispatchWorkItem?
    private var refreshGeneration = 0

    override init() {
        let savedPath = UserDefaults.standard.string(forKey: "ConfigDirectory") ?? defaultConfigPath
        currentDirectory = URL(fileURLWithPath: savedPath, isDirectory: true)
        super.init()
    }

    func applicationDidFinishLaunching(_ notification: Notification) {
        configureMainMenu()
        let configuration = WKWebViewConfiguration()
        configuration.userContentController.add(self, name: "configTool")
        webView = WKWebView(frame: .zero, configuration: configuration)
        webView.uiDelegate = self
        webView.setValue(false, forKey: "drawsBackground")

        window = NSWindow(
            contentRect: NSRect(x: 0, y: 0, width: 1380, height: 860),
            styleMask: [.titled, .closable, .miniaturizable, .resizable, .fullSizeContentView],
            backing: .buffered,
            defer: false
        )
        window.title = "PairPair ConfigTool"
        window.titlebarAppearsTransparent = true
        window.titleVisibility = .hidden
        window.isMovable = true
        window.minSize = NSSize(width: 960, height: 620)

        let rootView = NSView(frame: NSRect(x: 0, y: 0, width: 1380, height: 860))
        webView.frame = rootView.bounds
        webView.autoresizingMask = [.width, .height]
        rootView.addSubview(webView)

        let dragRegion = WindowDragView(frame: NSRect(
            x: 78,
            y: rootView.bounds.height - 34,
            width: rootView.bounds.width - 78,
            height: 34
        ))
        dragRegion.autoresizingMask = [.width, .minYMargin]
        rootView.addSubview(dragRegion)
        window.contentView = rootView
        window.center()
        window.makeKeyAndOrderFront(nil)

        guard let resourceURL = Bundle.main.resourceURL?.appendingPathComponent("Web/index.html") else {
            showError("应用资源缺失，请重新构建 ConfigTool。")
            return
        }
        webView.loadFileURL(resourceURL, allowingReadAccessTo: resourceURL.deletingLastPathComponent())
        NSApp.activate(ignoringOtherApps: true)
    }

    private func configureMainMenu() {
        let mainMenu = NSMenu()

        let appMenuItem = NSMenuItem()
        let appMenu = NSMenu()
        appMenu.addItem(withTitle: "退出 ConfigTool", action: #selector(NSApplication.terminate(_:)), keyEquivalent: "q")
        appMenuItem.submenu = appMenu
        mainMenu.addItem(appMenuItem)

        let editMenuItem = NSMenuItem(title: "编辑", action: nil, keyEquivalent: "")
        let editMenu = NSMenu(title: "编辑")
        addEditCommand(to: editMenu, title: "全选", action: "selectAll:", key: "a", modifiers: [.command])
        addEditCommand(to: editMenu, title: "剪切", action: "cut:", key: "x", modifiers: [.command])
        addEditCommand(to: editMenu, title: "拷贝", action: "copy:", key: "c", modifiers: [.command])
        addEditCommand(to: editMenu, title: "粘贴", action: "paste:", key: "v", modifiers: [.command])
        editMenu.addItem(.separator())
        addEditCommand(to: editMenu, title: "全选（Ctrl+A）", action: "selectAll:", key: "a", modifiers: [.control])
        addEditCommand(to: editMenu, title: "剪切（Ctrl+X）", action: "cut:", key: "x", modifiers: [.control])
        addEditCommand(to: editMenu, title: "拷贝（Ctrl+C）", action: "copy:", key: "c", modifiers: [.control])
        addEditCommand(to: editMenu, title: "粘贴（Ctrl+V）", action: "paste:", key: "v", modifiers: [.control])
        editMenuItem.submenu = editMenu
        mainMenu.addItem(editMenuItem)

        NSApp.mainMenu = mainMenu
    }

    private func addEditCommand(
        to menu: NSMenu,
        title: String,
        action: String,
        key: String,
        modifiers: NSEvent.ModifierFlags
    ) {
        let item = NSMenuItem(title: title, action: Selector(action), keyEquivalent: key)
        item.keyEquivalentModifierMask = modifiers
        item.target = nil
        menu.addItem(item)
    }

    func applicationDidBecomeActive(_ notification: Notification) {
        refreshIfChanged()
        refreshGitStatus()
    }

    func applicationShouldTerminateAfterLastWindowClosed(_ sender: NSApplication) -> Bool { true }

    func webView(
        _ webView: WKWebView,
        runJavaScriptConfirmPanelWithMessage message: String,
        initiatedByFrame frame: WKFrameInfo,
        completionHandler: @escaping (Bool) -> Void
    ) {
        let alert = NSAlert()
        alert.messageText = "未保存的修改"
        alert.informativeText = message
        alert.alertStyle = .warning
        alert.addButton(withTitle: "放弃修改")
        alert.addButton(withTitle: "继续编辑")
        alert.beginSheetModal(for: window) { response in
            completionHandler(response == .alertFirstButtonReturn)
        }
    }

    func userContentController(_ userContentController: WKUserContentController, didReceive message: WKScriptMessage) {
        guard let body = message.body as? [String: Any], let action = body["action"] as? String else { return }
        switch action {
        case "ready":
            refresh(force: true)
        case "refresh":
            refresh(force: true)
        case "chooseDirectory":
            chooseDirectory()
        case "switchDirectory":
            if let path = body["path"] as? String { switchDirectory(path: path) }
        case "revealDirectory":
            NSWorkspace.shared.activateFileViewerSelecting([currentDirectory])
        case "readClipboard":
            readClipboard(requestId: body["requestId"] as? Int ?? -1)
        case "writeClipboard":
            writeClipboard(text: body["text"] as? String ?? "")
        case "save":
            save(body: body)
        case "loadWorkbook":
            if let id = body["id"] as? String { loadWorkbook(id: id) }
        case "findReverseReferences":
            findReverseReferences(body: body)
        case "findGlobalMatches":
            findGlobalMatches(body: body)
        case "refreshGitStatus":
            refreshGitStatus()
        case "pullGit":
            pullGit()
        case "previewGitClean":
            previewGitClean()
        case "cleanGitChanges":
            cleanGitChanges(
                trackedPaths: body["trackedPaths"] as? [String] ?? [],
                untrackedPaths: body["untrackedPaths"] as? [String] ?? []
            )
        default:
            break
        }
    }

    private func chooseDirectory() {
        let panel = NSOpenPanel()
        panel.title = "选择配置表目录"
        panel.message = "请选择包含 .xlsx 或 .lua 配置的目录"
        panel.canChooseDirectories = true
        panel.canChooseFiles = false
        panel.allowsMultipleSelection = false
        panel.directoryURL = currentDirectory
        panel.beginSheetModal(for: window) { [weak self] response in
            guard response == .OK, let url = panel.url else { return }
            self?.currentDirectory = url
            UserDefaults.standard.set(url.path, forKey: "ConfigDirectory")
            self?.refresh(force: true)
        }
    }

    private func readClipboard(requestId: Int) {
        let text = NSPasteboard.general.string(forType: .string) ?? ""
        sendJavaScript(function: "receiveClipboardText", object: ["requestId": requestId, "text": text])
    }

    private func writeClipboard(text: String) {
        let pasteboard = NSPasteboard.general
        pasteboard.clearContents()
        pasteboard.setString(text, forType: .string)
    }

    private func switchDirectory(path: String) {
        let normalizedPath = (path as NSString).standardizingPath
        var isDirectory: ObjCBool = false
        guard FileManager.default.fileExists(atPath: normalizedPath, isDirectory: &isDirectory),
              isDirectory.boolValue else {
            sendJavaScript(function: "directorySwitchFailed", object: [
                "message": "预设目录不存在或不是文件夹：\(normalizedPath)"
            ])
            return
        }
        currentDirectory = URL(fileURLWithPath: normalizedPath, isDirectory: true)
        currentSignature = ""
        UserDefaults.standard.set(normalizedPath, forKey: "ConfigDirectory")
        refresh(force: true)
    }

    private func save(body: [String: Any]) {
        guard let id = body["id"] as? String,
              let signature = body["sourceSignature"] as? String,
              let rawChanges = body["changes"] as? [[String: Any]],
              let data = try? JSONSerialization.data(withJSONObject: rawChanges),
              let changes = try? JSONDecoder().decode([CellChange].self, from: data),
              !changes.isEmpty else {
            sendJavaScript(function: "saveFailed", object: ["message": "没有可保存的修改。"])
            return
        }
        sendJavaScript(function: "setSaving", object: ["saving": true])
        let directory = currentDirectory
        loaderQueue.async { [weak self] in
            guard let self else { return }
            do {
                try self.saver.save(
                    directory: directory,
                    id: id,
                    expectedSignature: signature,
                    changes: changes
                )
                let fileName = id.components(separatedBy: "::").first ?? ""
                self.loader.invalidate(path: directory.appendingPathComponent(fileName).path)
                DispatchQueue.main.async {
                    guard directory == self.currentDirectory else { return }
                    self.currentSignature = ""
                    self.sendJavaScript(function: "saveSucceeded", object: ["id": id])
                    self.refresh(force: true)
                }
            } catch {
                DispatchQueue.main.async {
                    self.sendJavaScript(function: "saveFailed", object: ["message": error.localizedDescription])
                }
            }
        }
    }

    private func loadWorkbook(id: String) {
        let directory = currentDirectory
        loaderQueue.async { [weak self] in
            guard let self else { return }
            do {
                let workbook = try self.loader.loadWorkbook(directory: directory, id: id)
                let data = try JSONEncoder().encode(workbook)
                let object = try JSONSerialization.jsonObject(with: data)
                DispatchQueue.main.async {
                    guard directory == self.currentDirectory else { return }
                    self.sendJavaScript(function: "receiveWorkbook", object: object)
                }
            } catch {
                DispatchQueue.main.async {
                    guard directory == self.currentDirectory else { return }
                    self.sendJavaScript(function: "receiveWorkbookError", object: [
                        "id": id,
                        "message": error.localizedDescription
                    ])
                }
            }
        }
    }

    private func findReverseReferences(body: [String: Any]) {
        guard let requestId = body["requestId"] as? Int,
              let value = body["value"] as? String,
              let targetTokens = body["targetTokens"] as? [String],
              let scalarFields = body["scalarFields"] as? [String],
              let jsonFields = body["jsonFields"] as? [String] else {
            sendJavaScript(function: "receiveReverseReferenceError", object: [
                "requestId": body["requestId"] as? Int ?? -1,
                "message": "反向引用查询参数无效"
            ])
            return
        }
        let relationRules = (body["relationRules"] as? [[String: Any]] ?? [])
            .map(RelationRule.init(dictionary:))
            .filter { !$0.fields.isEmpty }
        let directory = currentDirectory
        loaderQueue.async { [weak self] in
            guard let self else { return }
            do {
                let references = try self.loader.findReverseReferences(
                    directory: directory,
                    value: value,
                    targetTokens: targetTokens,
                    scalarFields: scalarFields,
                    jsonFields: jsonFields,
                    relationRules: relationRules
                )
                let response = ReverseReferenceResponse(
                    requestId: requestId,
                    value: value,
                    references: references
                )
                let data = try JSONEncoder().encode(response)
                let object = try JSONSerialization.jsonObject(with: data)
                DispatchQueue.main.async {
                    guard directory == self.currentDirectory else { return }
                    self.sendJavaScript(function: "receiveReverseReferences", object: object)
                }
            } catch {
                DispatchQueue.main.async {
                    guard directory == self.currentDirectory else { return }
                    self.sendJavaScript(function: "receiveReverseReferenceError", object: [
                        "requestId": requestId,
                        "message": error.localizedDescription
                    ])
                }
            }
        }
    }

    private func findGlobalMatches(body: [String: Any]) {
        guard let requestId = body["requestId"] as? Int,
              let query = body["query"] as? String else {
            sendJavaScript(function: "receiveGlobalSearchError", object: [
                "requestId": body["requestId"] as? Int ?? -1,
                "message": "全局搜索参数无效"
            ])
            return
        }
        let directory = currentDirectory
        loaderQueue.async { [weak self] in
            guard let self else { return }
            do {
                let result = try self.loader.findGlobalMatches(
                    directory: directory,
                    query: query
                )
                let response = GlobalSearchResponse(
                    requestId: requestId,
                    query: query,
                    totalCount: result.totalCount,
                    matches: result.matches
                )
                let data = try JSONEncoder().encode(response)
                let object = try JSONSerialization.jsonObject(with: data)
                DispatchQueue.main.async {
                    guard directory == self.currentDirectory else { return }
                    self.sendJavaScript(function: "receiveGlobalSearchResults", object: object)
                }
            } catch {
                DispatchQueue.main.async {
                    guard directory == self.currentDirectory else { return }
                    self.sendJavaScript(function: "receiveGlobalSearchError", object: [
                        "requestId": requestId,
                        "message": error.localizedDescription
                    ])
                }
            }
        }
    }

    private func refreshGitStatus() {
        let directory = currentDirectory
        loaderQueue.async { [weak self] in
            guard let self else { return }
            let status = self.gitRepository.inspect(directory: directory)
            DispatchQueue.main.async {
                guard directory == self.currentDirectory else { return }
                self.sendCodableJavaScript(function: "receiveGitStatus", value: status)
            }
        }
    }

    private func pullGit() {
        let directory = currentDirectory
        sendJavaScript(function: "setGitOperation", object: ["operation": "pull", "running": true])
        loaderQueue.async { [weak self] in
            guard let self else { return }
            let result = self.gitRepository.pull(directory: directory)
            DispatchQueue.main.async {
                guard directory == self.currentDirectory else { return }
                self.sendCodableJavaScript(
                    function: "receiveGitOperation",
                    value: GitOperationResponse(operation: "pull", success: result.success, message: result.message, status: result.status)
                )
                if result.success {
                    self.currentSignature = ""
                    self.refresh(force: true)
                }
            }
        }
    }

    private func previewGitClean() {
        let directory = currentDirectory
        sendJavaScript(function: "setGitOperation", object: ["operation": "previewClean", "running": true])
        loaderQueue.async { [weak self] in
            guard let self else { return }
            let preview = self.gitRepository.previewClean(directory: directory)
            DispatchQueue.main.async {
                guard directory == self.currentDirectory else { return }
                self.sendCodableJavaScript(function: "receiveGitCleanPreview", value: preview)
            }
        }
    }

    private func cleanGitChanges(trackedPaths: [String], untrackedPaths: [String]) {
        let directory = currentDirectory
        sendJavaScript(function: "setGitOperation", object: ["operation": "clean", "running": true])
        loaderQueue.async { [weak self] in
            guard let self else { return }
            let result = self.gitRepository.clean(directory: directory, trackedPaths: trackedPaths, untrackedPaths: untrackedPaths)
            DispatchQueue.main.async {
                guard directory == self.currentDirectory else { return }
                self.sendCodableJavaScript(
                    function: "receiveGitOperation",
                    value: GitOperationResponse(operation: "clean", success: result.success, message: result.message, status: result.status)
                )
                if result.success {
                    self.currentSignature = ""
                    self.refresh(force: true)
                }
            }
        }
    }

    private func refreshIfChanged() {
        let signature = loader.signature(for: currentDirectory)
        if !signature.isEmpty && signature != currentSignature {
            refresh(force: true)
        }
    }

    private func scheduleRefresh() {
        reloadWorkItem?.cancel()
        let workItem = DispatchWorkItem { [weak self] in
            DispatchQueue.main.async { self?.refresh(force: false) }
        }
        reloadWorkItem = workItem
        DispatchQueue.global(qos: .utility).asyncAfter(deadline: .now() + 0.45, execute: workItem)
    }

    private func refresh(force: Bool) {
        guard FileManager.default.fileExists(atPath: currentDirectory.path) else {
            sendJavaScript(function: "receiveError", object: ["message": "配置目录不存在，请重新选择目录。"])
            return
        }
        let signature = loader.signature(for: currentDirectory)
        if !force && signature == currentSignature { return }
        refreshGeneration += 1
        let generation = refreshGeneration
        let directory = currentDirectory
        sendJavaScript(function: "setLoading", object: ["loading": true])

        loaderQueue.async { [weak self] in
            guard let self else { return }
            do {
                let payload = try self.loader.load(directory: directory)
                let gitStatus = self.gitRepository.inspect(directory: directory)
                let data = try JSONEncoder().encode(payload)
                let object = try JSONSerialization.jsonObject(with: data)
                DispatchQueue.main.async {
                    guard generation == self.refreshGeneration,
                          directory == self.currentDirectory else { return }
                    self.currentSignature = signature
                    self.sendJavaScript(function: "receiveData", object: object)
                    self.sendCodableJavaScript(function: "receiveGitStatus", value: gitStatus)
                    self.monitor.watch(path: directory.path) { [weak self] in self?.scheduleRefresh() }
                }
            } catch {
                DispatchQueue.main.async {
                    guard generation == self.refreshGeneration,
                          directory == self.currentDirectory else { return }
                    self.sendJavaScript(function: "receiveError", object: ["message": error.localizedDescription])
                }
            }
        }
    }

    private func sendJavaScript(function: String, object: Any) {
        guard JSONSerialization.isValidJSONObject(object),
              let data = try? JSONSerialization.data(withJSONObject: object),
              let json = String(data: data, encoding: .utf8) else { return }
        webView.evaluateJavaScript("window.ConfigTool.\(function)(\(json));")
    }

    private func sendCodableJavaScript<Value: Encodable>(function: String, value: Value) {
        guard let data = try? JSONEncoder().encode(value),
              let object = try? JSONSerialization.jsonObject(with: data) else { return }
        sendJavaScript(function: function, object: object)
    }

    private func showError(_ message: String) {
        let alert = NSAlert()
        alert.messageText = "ConfigTool"
        alert.informativeText = message
        alert.runModal()
    }
}

private func runAudit(path: String) -> Int32 {
    do {
        let payload = try DirectoryLoader().load(
            directory: URL(fileURLWithPath: path, isDirectory: true),
            includeRows: true
        )
        let failures = payload.workbooks.filter { $0.error != nil }
        let totalRows = payload.workbooks.reduce(0) { $0 + $1.rowCount }
        let totalCells = payload.workbooks.reduce(0) { total, workbook in
            total + workbook.rows.reduce(0) { $0 + $1.filter { !$0.isEmpty }.count }
        }
        var report = "files=\(payload.fileCount) sheets=\(payload.workbooks.count) rows=\(totalRows) nonEmptyCells=\(totalCells) failures=\(failures.count)\n"
        failures.forEach { report += "ERROR \($0.fileName): \($0.error ?? "")\n" }
        FileHandle.standardOutput.write(Data(report.utf8))
        return failures.isEmpty ? 0 : 2
    } catch {
        FileHandle.standardError.write(Data("ConfigTool audit failed: \(error.localizedDescription)\n".utf8))
        return 1
    }
}

private func runReverseAudit(
    path: String,
    value: String,
    targetToken: String,
    scalarFields: [String],
    jsonFields: [String]
) -> Int32 {
    do {
        let references = try DirectoryLoader().findReverseReferences(
            directory: URL(fileURLWithPath: path, isDirectory: true),
            value: value,
            targetTokens: [targetToken],
            scalarFields: scalarFields,
            jsonFields: jsonFields
        )
        let data = try JSONEncoder().encode(references)
        FileHandle.standardOutput.write(data)
        FileHandle.standardOutput.write(Data("\n".utf8))
        return 0
    } catch {
        FileHandle.standardError.write(Data("ConfigTool reverse audit failed: \(error.localizedDescription)\n".utf8))
        return 1
    }
}

private func runGlobalSearchAudit(path: String, query: String) -> Int32 {
    do {
        let result = try DirectoryLoader().findGlobalMatches(
            directory: URL(fileURLWithPath: path, isDirectory: true),
            query: query
        )
        let response = GlobalSearchResponse(
            requestId: 0,
            query: query,
            totalCount: result.totalCount,
            matches: result.matches
        )
        let data = try JSONEncoder().encode(response)
        FileHandle.standardOutput.write(data)
        FileHandle.standardOutput.write(Data("\n".utf8))
        return 0
    } catch {
        FileHandle.standardError.write(Data("ConfigTool global search audit failed: \(error.localizedDescription)\n".utf8))
        return 1
    }
}

private func runGitAudit(path: String) -> Int32 {
    let status = GitRepositoryService().inspect(directory: URL(fileURLWithPath: path, isDirectory: true))
    do {
        let data = try JSONEncoder().encode(status)
        FileHandle.standardOutput.write(data)
        FileHandle.standardOutput.write(Data("\n".utf8))
        return status.isRepository ? 0 : 2
    } catch {
        FileHandle.standardError.write(Data("ConfigTool Git audit failed: \(error.localizedDescription)\n".utf8))
        return 1
    }
}

private func runGitSelfTest() -> Int32 {
    let directory = FileManager.default.temporaryDirectory.appendingPathComponent("PairPairConfigTool-Git-\(UUID().uuidString)", isDirectory: true)
    defer { try? FileManager.default.removeItem(at: directory) }
    do {
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        let repository = directory.appendingPathComponent("clean-test", isDirectory: true)
        try FileManager.default.createDirectory(at: repository, withIntermediateDirectories: true)
        try runSystemGit(in: repository, arguments: ["init", "-q"])
        try runSystemGit(in: repository, arguments: ["config", "user.email", "configtool-self-test@example.invalid"])
        try runSystemGit(in: repository, arguments: ["config", "user.name", "ConfigTool Self Test"])
        let tracked = repository.appendingPathComponent("tracked.txt")
        let trackedKeep = repository.appendingPathComponent("tracked-keep.txt")
        let untracked = repository.appendingPathComponent("untracked.txt")
        let untrackedKeep = repository.appendingPathComponent("untracked-keep.txt")
        try "before\n".write(to: tracked, atomically: true, encoding: .utf8)
        try "keep-before\n".write(to: trackedKeep, atomically: true, encoding: .utf8)
        try runSystemGit(in: repository, arguments: ["add", "tracked.txt", "tracked-keep.txt"])
        try runSystemGit(in: repository, arguments: ["commit", "-qm", "initial"])
        try "changed\n".write(to: tracked, atomically: true, encoding: .utf8)
        try "keep-changed\n".write(to: trackedKeep, atomically: true, encoding: .utf8)
        try "untracked\n".write(to: untracked, atomically: true, encoding: .utf8)
        try "keep-untracked\n".write(to: untrackedKeep, atomically: true, encoding: .utf8)
        let git = GitRepositoryService()
        let dirty = git.inspect(directory: repository)
        guard dirty.isRepository, dirty.trackedChangeCount == 2, dirty.untrackedCount == 2 else {
            throw NSError(domain: "ConfigTool.GitSelfTest", code: 1, userInfo: [NSLocalizedDescriptionKey: "Git 工作区状态识别不正确"])
        }
        let preview = git.previewClean(directory: repository)
        guard preview.untrackedPaths.contains("untracked.txt"), preview.untrackedPaths.contains("untracked-keep.txt") else {
            throw NSError(domain: "ConfigTool.GitSelfTest", code: 2, userInfo: [NSLocalizedDescriptionKey: "Git 未跟踪文件预览不正确"])
        }
        let restore = git.clean(directory: repository, trackedPaths: ["tracked.txt"], untrackedPaths: [])
        guard restore.success,
              try String(contentsOf: tracked, encoding: .utf8) == "before\n",
              try String(contentsOf: trackedKeep, encoding: .utf8) == "keep-changed\n",
              FileManager.default.fileExists(atPath: untracked.path) else {
            throw NSError(domain: "ConfigTool.GitSelfTest", code: 3, userInfo: [NSLocalizedDescriptionKey: "Git 单文件恢复不正确"])
        }
        let delete = git.clean(directory: repository, trackedPaths: [], untrackedPaths: ["untracked.txt"])
        guard delete.success,
              !FileManager.default.fileExists(atPath: untracked.path),
              FileManager.default.fileExists(atPath: untrackedKeep.path),
              try String(contentsOf: trackedKeep, encoding: .utf8) == "keep-changed\n" else {
            throw NSError(domain: "ConfigTool.GitSelfTest", code: 4, userInfo: [NSLocalizedDescriptionKey: "Git 单文件删除不正确"])
        }
        let finishClean = git.clean(directory: repository, trackedPaths: ["tracked-keep.txt"], untrackedPaths: ["untracked-keep.txt"])
        guard finishClean.success,
              try String(contentsOf: trackedKeep, encoding: .utf8) == "keep-before\n",
              !FileManager.default.fileExists(atPath: untrackedKeep.path) else {
            throw NSError(domain: "ConfigTool.GitSelfTest", code: 5, userInfo: [NSLocalizedDescriptionKey: "Git 选择清理收尾不正确"])
        }

        let remote = directory.appendingPathComponent("remote.git", isDirectory: true)
        let producer = directory.appendingPathComponent("producer", isDirectory: true)
        let consumer = directory.appendingPathComponent("consumer", isDirectory: true)
        try runSystemGit(in: directory, arguments: ["init", "--bare", "-q", remote.path])
        try FileManager.default.createDirectory(at: producer, withIntermediateDirectories: true)
        try runSystemGit(in: producer, arguments: ["init", "-q"])
        try runSystemGit(in: producer, arguments: ["config", "user.email", "configtool-self-test@example.invalid"])
        try runSystemGit(in: producer, arguments: ["config", "user.name", "ConfigTool Self Test"])
        let shared = producer.appendingPathComponent("shared.txt")
        try "first\n".write(to: shared, atomically: true, encoding: .utf8)
        try runSystemGit(in: producer, arguments: ["add", "shared.txt"])
        try runSystemGit(in: producer, arguments: ["commit", "-qm", "first"])
        try runSystemGit(in: producer, arguments: ["branch", "-M", "main"])
        try runSystemGit(in: producer, arguments: ["remote", "add", "origin", remote.path])
        try runSystemGit(in: producer, arguments: ["push", "-qu", "origin", "main"])
        try runSystemGit(in: directory, arguments: ["--git-dir", remote.path, "symbolic-ref", "HEAD", "refs/heads/main"])
        try runSystemGit(in: directory, arguments: ["clone", "-q", remote.path, consumer.path])
        try "second\n".write(to: shared, atomically: true, encoding: .utf8)
        try runSystemGit(in: producer, arguments: ["commit", "-am", "second", "-q"])
        try runSystemGit(in: producer, arguments: ["push", "-q", "origin", "main"])
        let pull = git.pull(directory: consumer)
        guard pull.success, try String(contentsOf: consumer.appendingPathComponent("shared.txt"), encoding: .utf8) == "second\n" else {
            throw NSError(domain: "ConfigTool.GitSelfTest", code: 6, userInfo: [NSLocalizedDescriptionKey: "Git 快进拉取不正确"])
        }
        FileHandle.standardOutput.write(Data("git_self_test=ok clean=ok pull=ok\n".utf8))
        return 0
    } catch {
        FileHandle.standardError.write(Data("ConfigTool Git self test failed: \(error.localizedDescription)\n".utf8))
        return 1
    }
}

private func runSystemGit(in directory: URL, arguments: [String]) throws {
    let process = Process()
    let errors = Pipe()
    process.executableURL = URL(fileURLWithPath: "/usr/bin/git")
    process.currentDirectoryURL = directory
    process.arguments = arguments
    process.standardOutput = FileHandle.nullDevice
    process.standardError = errors
    try process.run()
    process.waitUntilExit()
    guard process.terminationStatus == 0 else {
        let message = String(data: errors.fileHandleForReading.readDataToEndOfFile(), encoding: .utf8) ?? "Git 命令失败"
        throw NSError(domain: "ConfigTool.GitSelfTest", code: Int(process.terminationStatus), userInfo: [NSLocalizedDescriptionKey: message])
    }
}

if let auditIndex = CommandLine.arguments.firstIndex(of: "--audit") {
    let path = CommandLine.arguments.indices.contains(auditIndex + 1) ? CommandLine.arguments[auditIndex + 1] : defaultConfigPath
    exit(runAudit(path: path))
}

if let auditIndex = CommandLine.arguments.firstIndex(of: "--reverse-audit") {
    let arguments = CommandLine.arguments
    let path = arguments.indices.contains(auditIndex + 1) ? arguments[auditIndex + 1] : defaultConfigPath
    let value = arguments.indices.contains(auditIndex + 2) ? arguments[auditIndex + 2] : ""
    let targetToken = arguments.indices.contains(auditIndex + 3) ? arguments[auditIndex + 3] : ""
    let scalarFields = arguments.indices.contains(auditIndex + 4)
        ? arguments[auditIndex + 4].split(separator: ",").map(String.init)
        : []
    let jsonFields = arguments.indices.contains(auditIndex + 5)
        ? arguments[auditIndex + 5].split(separator: ",").map(String.init)
        : []
    exit(runReverseAudit(
        path: path,
        value: value,
        targetToken: targetToken,
        scalarFields: scalarFields,
        jsonFields: jsonFields
    ))
}

if let auditIndex = CommandLine.arguments.firstIndex(of: "--global-search-audit") {
    let arguments = CommandLine.arguments
    let path = arguments.indices.contains(auditIndex + 1) ? arguments[auditIndex + 1] : defaultConfigPath
    let query = arguments.indices.contains(auditIndex + 2) ? arguments[auditIndex + 2] : ""
    exit(runGlobalSearchAudit(path: path, query: query))
}

if let auditIndex = CommandLine.arguments.firstIndex(of: "--git-audit") {
    let path = CommandLine.arguments.indices.contains(auditIndex + 1) ? CommandLine.arguments[auditIndex + 1] : defaultConfigPath
    exit(runGitAudit(path: path))
}

if CommandLine.arguments.contains("--git-self-test") {
    exit(runGitSelfTest())
}

private let application = NSApplication.shared
private let controller = AppController()
application.delegate = controller
application.setActivationPolicy(.regular)
application.run()
