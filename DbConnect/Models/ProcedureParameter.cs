namespace DbConnect.Models;

public enum ParameterDirection
{
    In,
    Out,
    InOut
}

public class ProcedureParameter
{
    public string Name { get; set; } = string.Empty;
    public string DataType { get; set; } = string.Empty;
    public ParameterDirection Direction { get; set; } = ParameterDirection.In;
    public string? DefaultValue { get; set; }
    public string? RuntimeValue { get; set; }
    public int OrdinalPosition { get; set; }
}
