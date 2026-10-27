import CoreFoundation
import Foundation

// Three header rows match the existing spreadsheet and Lua view contract.
final class JsonConfigParser {
    func parse(url: URL) throws -> [WorkbookView] {
        let root = try JSONSerialization.jsonObject(with: Data(contentsOf: url), options: [.fragmentsAllowed])
        let baseName = url.deletingPathExtension().lastPathComponent
        let tables: [(String, Any)]
        if let object = root as? [String: Any] {
            let wrapped = object.keys.sorted().filter {
                ($0.hasSuffix("Map") || $0.hasSuffix("Slice")) &&
                (object[$0] is [String: Any] || object[$0] is [Any])
            }
            tables = wrapped.isEmpty ? [(baseName, object)] : wrapped.map { ($0, object[$0]!) }
        } else if root is [Any] {
            tables = [(baseName, root)]
        } else {
            throw NSError(domain: "ConfigTool.JSON", code: 1, userInfo: [NSLocalizedDescriptionKey: "JSON 配置顶层必须是对象或数组"])
        }
        let attributes = try FileManager.default.attributesOfItem(atPath: url.path)
        let modified = attributes[.modificationDate] as? Date ?? .distantPast
        let formatter = ISO8601DateFormatter()
        formatter.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
        return try tables.map { tableName, value in
            let matrix = try buildMatrix(value)
            return WorkbookView(
                id: "\(url.lastPathComponent)::\(tableName)", name: baseName,
                fileName: url.lastPathComponent, sheetName: tableName,
                sheetCount: tables.count, category: ConfigCategory.name(for: baseName),
                modifiedAt: formatter.string(from: modified), rowCount: matrix.count,
                columnCount: matrix[1].count, rows: matrix, sourceKind: "json",
                sourceSignature: configFileSignature(url), editableFromRow: 3,
                lockedCells: lockedCells(value, rows: matrix), isLoaded: true, error: nil
            )
        }
    }

    private func buildMatrix(_ value: Any) throws -> [[String]] {
        let entries: [(String, Any)]
        let keyHeader: String
        if let array = value as? [Any] {
            entries = array.enumerated().map { (String($0.offset + 1), $0.element) }
            keyHeader = "__index"
        } else if let object = value as? [String: Any] {
            if object.isEmpty || object.values.allSatisfy({ $0 is [String: Any] }) {
                entries = object.keys.sorted().map { ($0, object[$0]!) }
            } else {
                entries = [("1", object)]
            }
            keyHeader = "__key"
        } else {
            entries = [("1", value)]
            keyHeader = "__key"
        }
        let records = entries.map { $0.1 as? [String: Any] ?? ["value": $0.1] }
        let fields = Set(records.flatMap { $0.keys }).sorted {
            let left = ["id", "key"].contains($0.lowercased())
            let right = ["id", "key"].contains($1.lowercased())
            return left != right ? left : $0 < $1
        }
        var syntheticKey = keyHeader
        while fields.contains(syntheticKey) { syntheticKey = "_" + syntheticKey }
        let types = fields.map { field in
            records.compactMap { $0[field] }.first(where: { !($0 is NSNull) }).map(typeName) ?? ""
        }
        var rows = [["键 / 序号"] + fields.map { _ in "" }, [syntheticKey] + fields, ["string"] + types]
        for (index, entry) in entries.enumerated() {
            rows.append([entry.0] + (try fields.map { field in
                guard let cell = records[index][field] else { return "" }
                return try display(cell)
            }))
        }
        return rows
    }

    private func lockedCells(_ value: Any, rows: [[String]]) -> [String] {
        let records: [Any]
        if let array = value as? [Any] { records = array }
        else if let object = value as? [String: Any], object.values.allSatisfy({ $0 is [String: Any] }) {
            records = object.keys.sorted().map { object[$0]! }
        } else { records = [value] }
        var locked: [String] = []
        for (index, record) in records.enumerated() {
            for column in rows[1].indices {
                if column == 0 || (record as? [String: Any])?[rows[1][column]] == nil {
                    locked.append("\(index + 3):\(column)")
                }
            }
        }
        return locked
    }

    private func typeName(_ value: Any) -> String {
        if value is String { return "string" }
        if let number = value as? NSNumber {
            return CFGetTypeID(number) == CFBooleanGetTypeID() ? "bool" : "number"
        }
        return "json"
    }

    private func display(_ value: Any) throws -> String {
        if let string = value as? String { return string }
        if let number = value as? NSNumber {
            return CFGetTypeID(number) == CFBooleanGetTypeID() ? (number.boolValue ? "true" : "false") : number.stringValue
        }
        let data = try JSONSerialization.data(withJSONObject: value, options: [.fragmentsAllowed, .sortedKeys, .withoutEscapingSlashes])
        return String(decoding: data, as: UTF8.self)
    }
}
