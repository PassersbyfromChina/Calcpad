namespace Calcpad.Core.Numerics;
/// <summary>
/// A number together with its unit. Every value that flows through the evaluator is a quantity, so
/// <c>3 m + 20 cm</c> stays <c>3.2 m</c> and <c>1 m / 1 s</c> becomes <c>1 m/s</c>.
/// </summary>
public readonly struct Quantity : IEquatable<Quantity>
{
    /// <summary>Creates a quantity expressed in <paramref name="unit"/>.</summary>
    public Quantity(double value, UnitExpr? unit)
        : this(value, unit, false)
    {
    }

    private Quantity(double value, UnitExpr? unit, bool isUnitLiteral)
    {
        Value = value;
        Unit = unit ?? UnitExpr.None;
        IsUnitLiteral = isUnitLiteral;
    }

    /// <summary>Numeric value, expressed in <see cref="Unit"/>.</summary>
    public double Value { get; }

    /// <summary>Unit the value is expressed in; <see cref="UnitExpr.None"/> for plain numbers.</summary>
    public UnitExpr Unit { get; }

    /// <summary>
    /// True when the quantity is a bare unit name such as <c>f</c> in <c>100 f</c>. Affine units may
    /// only be combined with a plain number in exactly this position, which is how a temperature is
    /// written down; scaling an existing temperature (for example <c>2 c * 3</c>) stays an error.
    /// </summary>
    public bool IsUnitLiteral { get; }

    /// <summary>True when the value carries no unit.</summary>
    public bool IsDimensionless => Unit.IsNone;

    /// <summary>True when the value carries a unit.</summary>
    public bool HasUnit => !Unit.IsNone;

    /// <summary>A plain number without a unit.</summary>
    public static Quantity Number(double value) => new(value, UnitExpr.None);

    /// <summary>A value of one in the given unit, used when a unit name stands alone.</summary>
    public static Quantity One(UnitExpr unit) => new(1, unit, true);

    /// <summary>Returns the numeric value or throws when a unit is attached.</summary>
    public double RequireNumber()
    {
        if (HasUnit)
        {
            throw new CalcException(Errors.ExpectedNumber(Unit.Symbol));
        }

        return Value;
    }

    /// <summary>Adds two quantities, converting the right operand into the left unit.</summary>
    public Quantity Add(Quantity other) => Combine(other, static (a, b) => a + b);

    /// <summary>Subtracts two quantities, converting the right operand into the left unit.</summary>
    public Quantity Subtract(Quantity other) => Combine(other, static (a, b) => a - b);

    /// <summary>Multiplies two quantities and composes their units.</summary>
    public Quantity Multiply(Quantity other)
    {
        RejectAffine(other);
        return new Quantity(Guards.Finite(Value * other.Value, string.Empty), MultiplyUnits(other));
    }

    /// <summary>Divides two quantities and composes their units.</summary>
    public Quantity Divide(Quantity other)
    {
        if (other.Value == 0)
        {
            throw new CalcException(Errors.DivideByZero);
        }

        RejectAffine(other);
        var unit = other.IsDimensionless ? Unit : UnitExpr.Divide(Unit, other.Unit);
        return new Quantity(Guards.Finite(Value / other.Value, string.Empty), unit);
    }

    /// <summary>Negates the value, keeping its unit.</summary>
    public Quantity Negate() => new(-Value, Unit);

    /// <summary>Raises the value to a power and scales the unit exponents.</summary>
    public Quantity Pow(double exponent)
    {
        if (Math.Abs(exponent) > Guards.MaxExponentMagnitude)
        {
            throw new CalcException(Errors.Overflow(string.Empty));
        }

        if (Value < 0 && Math.Abs(exponent - Math.Round(exponent)) > 1e-12)
        {
            throw new CalcException(Errors.Domain(string.Empty));
        }

        var unit = Math.Abs(exponent) < 1e-12 || IsDimensionless ? UnitExpr.None : UnitExpr.Pow(Unit, exponent);
        return new Quantity(Guards.Finite(Math.Pow(Value, exponent), string.Empty), unit);
    }

    /// <summary>Converts the value into another unit of the same dimension.</summary>
    public Quantity ConvertTo(UnitExpr target) => new(Unit.ConvertTo(Value, target), target);

    /// <summary>Formats the value with the requested number of significant digits and its unit.</summary>
    public string Format(int precision)
    {
        var text = NumberFormatter.Format(Value, precision);
        return IsDimensionless ? text : $"{text} {Unit.Symbol}";
    }

    /// <inheritdoc />
    public bool Equals(Quantity other) => Value.Equals(other.Value) && Unit == other.Unit;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is Quantity other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Value, Unit);

    /// <inheritdoc />
    public override string ToString() => Format(12);

    private Quantity Combine(Quantity other, Func<double, double, double> operation)
    {
        if (Unit.IsAffine || other.Unit.IsAffine)
        {
            throw new CalcException(Errors.AffineOperation);
        }

        if (IsDimensionless != other.IsDimensionless)
        {
            throw new CalcException(Errors.IncompatibleUnits(
                IsDimensionless ? "1" : Unit.Symbol,
                other.IsDimensionless ? "1" : other.Unit.Symbol));
        }

        if (IsDimensionless)
        {
            return new Quantity(Guards.Finite(operation(Value, other.Value), string.Empty), UnitExpr.None);
        }

        if (!Unit.IsCompatibleWith(other.Unit))
        {
            throw new CalcException(Errors.IncompatibleUnits(Unit.Symbol, other.Unit.Symbol));
        }

        // The right operand is expressed in its own unit; convert it into the left unit first.
        var converted = other.Unit.ConvertTo(other.Value, Unit);
        return new Quantity(Guards.Finite(operation(Value, converted), string.Empty), Unit);
    }

    private void RejectAffine(Quantity other)
    {
        if (!Unit.IsAffine && !other.Unit.IsAffine)
        {
            return;
        }

        if (Unit.IsAffine && other.Unit.IsAffine)
        {
            throw new CalcException(Errors.AffineOperation);
        }

        // "100 f" and "2 c" are unit literals scaled by a plain number and remain legal.
        var affineIsLiteral = Unit.IsAffine ? IsUnitLiteral : other.IsUnitLiteral;
        if (affineIsLiteral && (Unit.IsAffine ? other.IsDimensionless : IsDimensionless))
        {
            return;
        }

        throw new CalcException(Errors.AffineOperation);
    }

    private UnitExpr MultiplyUnits(Quantity other)
    {
        if (IsDimensionless)
        {
            return other.Unit;
        }

        return other.IsDimensionless ? Unit : UnitExpr.Multiply(Unit, other.Unit);
    }
}

/// <summary>
/// The unit catalogue backing the converter and the unit aware evaluator: 14 categories, at least
/// six units each, with English and Chinese names plus affine temperature conversions.
/// </summary>
public static class UnitCatalog
{
    private static readonly UnitCategory[] AllCategories =
    [
        Category("length", "Length", "长度",
            Unit("m", "Metre", "米", "m", 1),
            Unit("km", "Kilometre", "千米", "km", 1000),
            Unit("cm", "Centimetre", "厘米", "cm", 0.01),
            Unit("mm", "Millimetre", "毫米", "mm", 0.001),
            Unit("um", "Micrometre", "微米", "µm", 1e-6),
            Unit("nm", "Nanometre", "纳米", "nm", 1e-9),
            Unit("in", "Inch", "英寸", "in", 0.0254),
            Unit("ft", "Foot", "英尺", "ft", 0.3048),
            Unit("yd", "Yard", "码", "yd", 0.9144),
            Unit("mi", "Mile", "英里", "mi", 1609.344),
            Unit("nmi", "Nautical mile", "海里", "nmi", 1852),
            Unit("li", "Li", "里", "里", 500),
            Unit("chi", "Chi", "尺", "尺", 1d / 3),
            Unit("cun", "Cun", "寸", "寸", 1d / 30)),
        Category("mass", "Mass", "质量",
            Unit("kg", "Kilogram", "千克", "kg", 1),
            Unit("g", "Gram", "克", "g", 0.001),
            Unit("mg", "Milligram", "毫克", "mg", 1e-6),
            Unit("t", "Tonne", "吨", "t", 1000),
            Unit("lb", "Pound", "磅", "lb", 0.45359237),
            Unit("oz", "Ounce", "盎司", "oz", 0.028349523125),
            Unit("jin", "Jin", "斤", "斤", 0.5),
            Unit("liang", "Liang", "两", "两", 0.05),
            Unit("ct", "Carat", "克拉", "ct", 0.0002)),
        Category("temperature", "Temperature", "温度",
            Unit("k", "Kelvin", "开尔文", "K", 1),
            Unit("c", "Celsius", "摄氏度", "°C", 1, 273.15),
            Unit("f", "Fahrenheit", "华氏度", "°F", 5d / 9, 255.3722222222222),
            Unit("rankine", "Rankine", "兰氏度", "°R", 5d / 9, 0),
            Unit("reaumur", "Réaumur", "列氏度", "°Ré", 1.25, 273.15),
            Unit("delisle", "Delisle", "德利尔度", "°De", -2d / 3, 373.15)),
        Category("area", "Area", "面积",
            Unit("m2", "Square metre", "平方米", "m²", 1),
            Unit("km2", "Square kilometre", "平方千米", "km²", 1e6),
            Unit("cm2", "Square centimetre", "平方厘米", "cm²", 1e-4),
            Unit("mm2", "Square millimetre", "平方毫米", "mm²", 1e-6),
            Unit("ha", "Hectare", "公顷", "ha", 10000),
            Unit("mu", "Mu", "亩", "亩", 2000d / 3),
            Unit("acre", "Acre", "英亩", "acre", 4046.8564224),
            Unit("ft2", "Square foot", "平方英尺", "ft²", 0.09290304),
            Unit("in2", "Square inch", "平方英寸", "in²", 0.00064516),
            Unit("mi2", "Square mile", "平方英里", "mi²", 2589988.110336)),
        Category("volume", "Volume", "体积",
            Unit("l", "Litre", "升", "L", 1),
            Unit("ml", "Millilitre", "毫升", "mL", 0.001),
            Unit("m3", "Cubic metre", "立方米", "m³", 1000),
            Unit("cm3", "Cubic centimetre", "立方厘米", "cm³", 0.001),
            Unit("mm3", "Cubic millimetre", "立方毫米", "mm³", 1e-6),
            Unit("gal", "US gallon", "美制加仑", "gal", 3.785411784),
            Unit("qt", "US quart", "美制夸脱", "qt", 0.946352946),
            Unit("pt", "US pint", "美制品脱", "pt", 0.473176473),
            Unit("floz", "US fluid ounce", "美制液量盎司", "fl oz", 0.0295735295625),
            Unit("cup", "US cup", "杯", "cup", 0.2365882365),
            Unit("ft3", "Cubic foot", "立方英尺", "ft³", 28.316846592)),
        Category("speed", "Speed", "速度",
            Unit("mps", "Metre per second", "米每秒", "m/s", 1),
            Unit("kmh", "Kilometre per hour", "千米每小时", "km/h", 1000d / 3600),
            Unit("mph", "Mile per hour", "英里每小时", "mph", 1609.344 / 3600),
            Unit("fps", "Foot per second", "英尺每秒", "ft/s", 0.3048),
            Unit("kn", "Knot", "节", "kn", 1852d / 3600),
            Unit("mach", "Mach", "马赫", "mach", 340.29),
            Unit("cms", "Centimetre per second", "厘米每秒", "cm/s", 0.01)),
        Category("time", "Time", "时间",
            Unit("s", "Second", "秒", "s", 1),
            Unit("ms", "Millisecond", "毫秒", "ms", 0.001),
            Unit("us", "Microsecond", "微秒", "µs", 1e-6),
            Unit("min", "Minute", "分钟", "min", 60),
            Unit("h", "Hour", "小时", "h", 3600),
            Unit("d", "Day", "天", "d", 86400),
            Unit("wk", "Week", "周", "wk", 604800),
            Unit("mo", "Month", "月", "mo", 2629800),
            Unit("yr", "Year", "年", "yr", 31557600)),
        Category("energy", "Energy", "能量",
            Unit("j", "Joule", "焦耳", "J", 1),
            Unit("kj", "Kilojoule", "千焦", "kJ", 1000),
            Unit("cal", "Calorie", "卡", "cal", 4.184),
            Unit("kcal", "Kilocalorie", "千卡", "kcal", 4184),
            Unit("wh", "Watt hour", "瓦时", "Wh", 3600),
            Unit("kwh", "Kilowatt hour", "千瓦时", "kWh", 3600000),
            Unit("ev", "Electronvolt", "电子伏", "eV", 1.602176634e-19),
            Unit("btu", "British thermal unit", "英热单位", "BTU", 1055.05585262)),
        Category("power", "Power", "功率",
            Unit("w", "Watt", "瓦", "W", 1),
            Unit("kw", "Kilowatt", "千瓦", "kW", 1000),
            Unit("mw", "Megawatt", "兆瓦", "MW", 1e6),
            Unit("hp", "Horsepower", "英制马力", "hp", 745.6998715823),
            Unit("ps", "Metric horsepower", "公制马力", "PS", 735.49875),
            Unit("btuh", "BTU per hour", "英热单位每小时", "BTU/h", 1055.05585262 / 3600),
            Unit("ftlbs", "Foot pound per second", "英尺磅每秒", "ft·lb/s", 1.3558179483314)),
        Category("pressure", "Pressure", "压强",
            Unit("pa", "Pascal", "帕斯卡", "Pa", 1),
            Unit("kpa", "Kilopascal", "千帕", "kPa", 1000),
            Unit("mpa", "Megapascal", "兆帕", "MPa", 1e6),
            Unit("bar", "Bar", "巴", "bar", 100000),
            Unit("mbar", "Millibar", "毫巴", "mbar", 100),
            Unit("atm", "Atmosphere", "标准大气压", "atm", 101325),
            Unit("psi", "Pound per square inch", "磅每平方英寸", "psi", 6894.757293168),
            Unit("mmhg", "Millimetre of mercury", "毫米汞柱", "mmHg", 133.322387415),
            Unit("torr", "Torr", "托", "torr", 101325d / 760)),
        Category("data", "Data", "数据",
            Unit("bit", "Bit", "比特", "bit", 0.125),
            Unit("B", "Byte", "字节", "B", 1),
            Unit("KB", "Kilobyte", "千字节", "KB", 1000),
            Unit("MB", "Megabyte", "兆字节", "MB", 1e6),
            Unit("GB", "Gigabyte", "吉字节", "GB", 1e9),
            Unit("TB", "Terabyte", "太字节", "TB", 1e12),
            Unit("PB", "Petabyte", "拍字节", "PB", 1e15),
            Unit("KiB", "Kibibyte", "千二进制字节", "KiB", 1024),
            Unit("MiB", "Mebibyte", "兆二进制字节", "MiB", 1048576),
            Unit("GiB", "Gibibyte", "吉二进制字节", "GiB", 1073741824),
            Unit("TiB", "Tebibyte", "太二进制字节", "TiB", 1099511627776)),
        Category("angle", "Angle", "角度",
            Unit("rad", "Radian", "弧度", "rad", 1),
            Unit("deg", "Degree", "度", "°", Math.PI / 180),
            Unit("grad", "Gradian", "百分度", "grad", Math.PI / 200),
            Unit("turn", "Turn", "转", "turn", Math.PI * 2),
            Unit("arcmin", "Arcminute", "角分", "′", Math.PI / 10800),
            Unit("arcsec", "Arcsecond", "角秒", "″", Math.PI / 648000),
            Unit("mrad", "Milliradian", "毫弧度", "mrad", 0.001)),
        Category("force", "Force", "力",
            Unit("n", "Newton", "牛顿", "N", 1),
            Unit("kn", "Kilonewton", "千牛", "kN", 1000),
            Unit("kgf", "Kilogram force", "千克力", "kgf", 9.80665),
            Unit("lbf", "Pound force", "磅力", "lbf", 4.4482216152605),
            Unit("dyn", "Dyne", "达因", "dyn", 1e-5),
            Unit("pdl", "Poundal", "磅达", "pdl", 0.138254954376),
            Unit("gf", "Gram force", "克力", "gf", 0.00980665)),
        Category("frequency", "Frequency", "频率",
            Unit("hz", "Hertz", "赫兹", "Hz", 1),
            Unit("khz", "Kilohertz", "千赫兹", "kHz", 1000),
            Unit("mhz", "Megahertz", "兆赫兹", "MHz", 1e6),
            Unit("ghz", "Gigahertz", "吉赫兹", "GHz", 1e9),
            Unit("rpm", "Revolution per minute", "转每分钟", "rpm", 1d / 60),
            Unit("fps_hz", "Frame per second", "帧每秒", "FPS", 1))
    ];

    private static readonly Dictionary<string, UnitCategory> ById =
        AllCategories.ToDictionary(c => c.Id, StringComparer.OrdinalIgnoreCase);

    private static readonly Dictionary<string, Dimension> Dimensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ["length"] = Dimension.OfLength,
        ["mass"] = Dimension.OfMass,
        ["temperature"] = Dimension.OfTemperature,
        ["area"] = Dimension.OfLength.Scale(2),
        ["volume"] = Dimension.OfLength.Scale(3),
        ["speed"] = Dimension.OfLength.Subtract(Dimension.OfTime),
        ["time"] = Dimension.OfTime,
        ["energy"] = new Dimension(2, 1, -2, 0, 0, 0),
        ["power"] = new Dimension(2, 1, -3, 0, 0, 0),
        ["pressure"] = new Dimension(-1, 1, -2, 0, 0, 0),
        ["data"] = Dimension.OfData,
        ["angle"] = Dimension.OfAngle,
        ["force"] = new Dimension(1, 1, -2, 0, 0, 0),
        ["frequency"] = Dimension.OfTime.Scale(-1)
    };

    /// <summary>Every category the converter and the evaluator know about.</summary>
    public static IReadOnlyList<UnitCategory> Categories => AllCategories;

    /// <summary>Finds a category by id, for example <c>length</c> or <c>temperature</c>.</summary>
    public static UnitCategory? FindCategory(string? categoryId) =>
        categoryId is not null && ById.TryGetValue(categoryId, out var category) ? category : null;

    /// <summary>
    /// Finds a unit anywhere in the catalogue by id, symbol, English name or Chinese name.
    /// Matching is case insensitive; the first category in catalogue order wins.
    /// </summary>
    public static UnitInfo? FindUnit(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        var trimmed = token.Trim();
        foreach (var category in AllCategories)
        {
            var unit = category.Find(trimmed);
            if (unit is not null)
            {
                return unit;
            }
        }

        return null;
    }

    /// <summary>Finds the unit together with the category that owns it.</summary>
    public static (UnitCategory Category, UnitInfo Unit)? FindUnitWithCategory(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        var trimmed = token.Trim();
        foreach (var category in AllCategories)
        {
            var unit = category.Find(trimmed);
            if (unit is not null)
            {
                return (category, unit);
            }
        }

        return null;
    }

    /// <summary>The physical dimension every unit of a category shares.</summary>
    public static Dimension DimensionOf(string categoryId) =>
        Dimensions.TryGetValue(categoryId, out var dimension) ? dimension : Dimension.None;

    /// <summary>Dimension of the category that owns <paramref name="unit"/>.</summary>
    public static Dimension DimensionOf(UnitInfo unit)
    {
        foreach (var category in AllCategories)
        {
            if (category.Units.Contains(unit))
            {
                return DimensionOf(category.Id);
            }
        }

        return Dimension.None;
    }

    /// <summary>Builds the unit expression of a catalogue unit.</summary>
    public static UnitExpr ToExpression(UnitInfo unit) => UnitExpr.FromUnit(unit, DimensionOf(unit));

    /// <summary>Every unit symbol, name and id, for completion and handwriting correction.</summary>
    public static IReadOnlyList<string> AllNames()
    {
        var names = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var category in AllCategories)
        {
            foreach (var unit in category.Units)
            {
                Add(unit.Symbol);
                Add(unit.Id);
                Add(unit.NameZh);
                Add(unit.Name);
            }
        }

        return names;

        void Add(string value)
        {
            if (!string.IsNullOrWhiteSpace(value) && seen.Add(value))
            {
                names.Add(value);
            }
        }
    }

    private static UnitCategory Category(string id, string name, string nameZh, params UnitInfo[] units) =>
        new(id, name, nameZh, units);

    private static UnitInfo Unit(
        string id,
        string name,
        string nameZh,
        string symbol,
        double factor,
        double offset = 0) =>
        new(id, name, nameZh, symbol, factor, offset, factor == 1 && offset == 0);
}
