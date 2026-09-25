namespace MySale.AI.Application.Mql;

/// <summary>Whitelists and deny-lists used by <see cref="MqlValidator"/>.</summary>
public static class MqlOperators
{
    public static readonly HashSet<string> ReadOperations = new(StringComparer.Ordinal)
    {
        "find", "aggregate", "count", "distinct"
    };

    public static readonly HashSet<string> WriteOperations = new(StringComparer.OrdinalIgnoreCase)
    {
        "insert", "insertone", "insertmany", "update", "updateone", "updatemany", "replaceone",
        "delete", "deleteone", "deletemany", "remove", "drop", "dropdatabase", "rename", "renamecollection",
        "createindex", "createindexes", "dropindex", "dropindexes", "findandmodify", "findoneandupdate",
        "findoneanddelete", "findoneandreplace", "bulkwrite", "create", "createcollection", "eval",
        "runcommand", "command", "admincommand", "mapreduce", "shutdown", "grantroles", "createuser"
    };

    public static readonly HashSet<string> Stages = new(StringComparer.Ordinal)
    {
        "$match", "$group", "$sort", "$limit", "$skip", "$project", "$addFields", "$set", "$unset",
        "$unwind", "$count", "$lookup", "$sortByCount", "$facet"
    };

    /// <summary>Operators that are never allowed anywhere, with the reason shown to the tester.</summary>
    public static readonly Dictionary<string, string> Denied = new(StringComparer.Ordinal)
    {
        ["$out"] = "writes data",
        ["$merge"] = "writes data",
        ["$where"] = "executes JavaScript",
        ["$function"] = "executes JavaScript",
        ["$accumulator"] = "executes JavaScript",
        ["$unionWith"] = "reads other collections without tenant scoping",
        ["$graphLookup"] = "recursive cross-collection lookups are not allowed",
        ["$collStats"] = "database administration",
        ["$indexStats"] = "database administration",
        ["$currentOp"] = "database administration",
        ["$listSessions"] = "database administration",
        ["$listLocalSessions"] = "database administration",
        ["$planCacheStats"] = "database administration",
        ["$listSearchIndexes"] = "database administration",
        ["$shardedDataDistribution"] = "database administration",
        ["$changeStream"] = "database administration",
        ["$documents"] = "injects arbitrary documents",
        ["$text"] = "full-text search is not enabled",
        ["$search"] = "Atlas search is not enabled",
        ["$searchMeta"] = "Atlas search is not enabled",
        ["$vectorSearch"] = "vector search is not enabled",
        ["$jsonSchema"] = "not supported",
        ["$geoNear"] = "not supported",
        ["$replaceRoot"] = "not supported",
        ["$replaceWith"] = "not supported"
    };

    public static readonly HashSet<string> QueryOperators = new(StringComparer.Ordinal)
    {
        "$eq", "$ne", "$gt", "$gte", "$lt", "$lte", "$in", "$nin", "$exists", "$regex", "$options",
        "$not", "$elemMatch", "$size", "$type", "$all", "$mod"
    };

    public static readonly HashSet<string> LogicalQueryOperators = new(StringComparer.Ordinal)
    {
        "$and", "$or", "$nor"
    };

    public static readonly HashSet<string> Accumulators = new(StringComparer.Ordinal)
    {
        "$sum", "$avg", "$min", "$max", "$first", "$last", "$push", "$addToSet", "$count",
        "$stdDevPop", "$stdDevSamp", "$median", "$percentile", "$top", "$bottom", "$topN", "$bottomN",
        "$firstN", "$lastN", "$maxN", "$minN", "$mergeObjects"
    };

    public static readonly HashSet<string> ExpressionOperators = new(StringComparer.Ordinal)
    {
        // arithmetic
        "$add", "$subtract", "$multiply", "$divide", "$mod", "$abs", "$ceil", "$floor", "$round", "$trunc",
        "$sqrt", "$pow", "$exp", "$ln", "$log", "$log10",
        // comparison / boolean
        "$eq", "$ne", "$gt", "$gte", "$lt", "$lte", "$cmp", "$and", "$or", "$not",
        // conditional
        "$cond", "$ifNull", "$switch",
        // string
        "$concat", "$substr", "$substrBytes", "$substrCP", "$toLower", "$toUpper", "$trim", "$ltrim", "$rtrim",
        "$split", "$strLenCP", "$indexOfCP", "$regexMatch", "$replaceAll", "$replaceOne", "$strcasecmp",
        // array
        "$size", "$arrayElemAt", "$first", "$last", "$filter", "$map", "$in", "$isArray", "$slice",
        "$concatArrays", "$reverseArray", "$setUnion", "$setIntersection",
        // accumulators usable as expressions
        "$sum", "$avg", "$min", "$max", "$count", "$push", "$addToSet", "$stdDevPop", "$stdDevSamp",
        "$median", "$percentile", "$top", "$bottom", "$topN", "$bottomN", "$firstN", "$lastN", "$maxN", "$minN",
        // date
        "$year", "$month", "$dayOfMonth", "$dayOfWeek", "$dayOfYear", "$week", "$isoWeek", "$isoWeekYear",
        "$isoDayOfWeek", "$hour", "$minute", "$second", "$millisecond", "$dateToString", "$dateFromString",
        "$dateTrunc", "$dateAdd", "$dateSubtract", "$dateDiff", "$dateFromParts", "$dateToParts",
        // type
        "$toDouble", "$toInt", "$toLong", "$toDecimal", "$toString", "$toDate", "$toBool", "$toObjectId",
        "$convert", "$type", "$isNumber",
        // misc
        "$literal", "$let", "$mergeObjects", "$objectToArray", "$arrayToObject"
    };

    /// <summary>MongoDB Extended JSON literal wrappers, allowed only as single-key value objects.</summary>
    public static readonly HashSet<string> ExtendedJsonLiterals = new(StringComparer.Ordinal)
    {
        "$date", "$oid", "$numberDecimal", "$numberLong", "$numberInt", "$numberDouble"
    };

    public static readonly HashSet<string> DeniedVariables = new(StringComparer.Ordinal)
    {
        "USER_ROLES", "SEARCH_META", "CLUSTER_TIME"
    };
}
