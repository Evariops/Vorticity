using System;

namespace Vorticity.Tests;

// The records the tests read query results through: a selection of several values is read by
// As<TRecord>(), whose members take its elements in order.

/// <summary>A text key and its rows.</summary>
[VortexRecord]
public partial record struct CityCount(string City, long Count);

/// <summary>An integer key and its rows.</summary>
[VortexRecord]
public partial record struct KeyCount(long Key, long Count);

/// <summary>A key of the type matrix and its rows: an enum's, a char's, a 128-bit integer's, a wide decimal's.</summary>
[VortexRecord]
public partial record struct CharCount(char Key, long Count);

[VortexRecord]
public partial record struct WideIntegerCount([VortexColumn(Precision = 39)] Int128 Key, long Count);

[VortexRecord]
public partial record struct WideDecimalCount([VortexColumn(Precision = 76, Scale = 10)] VortexDecimal Key, long Count);

/// <summary>A sum of a nullable float column and the rows.</summary>
[VortexRecord]
public partial record struct SumAndCount(double Sum, long Count);

/// <summary>A text key, the rows a filter keeps of its group, and whether one row answers another.</summary>
[VortexRecord]
public partial record struct CityFiltered(string City, long Kept, bool Any);
