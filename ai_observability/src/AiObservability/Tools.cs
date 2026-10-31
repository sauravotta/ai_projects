using System.Globalization;

namespace AiObservability;

public static class Tools
{
    public static readonly string[] Names = { "calculator", "clock", "lookup" };

    public static async Task<string> RunAsync(string tool, string arg) => tool switch
    {
        "calculator" => Calculator.Evaluate(arg).ToString(CultureInfo.InvariantCulture),
        "clock" => DateTime.Now.ToString("s"),
        "lookup" => await SlowLookup(arg),
        _ => throw new ArgumentException("unknown tool " + tool),
    };

    // Deliberately slow "knowledge base" so latency outliers show up in the dashboard.
    private static async Task<string> SlowLookup(string arg)
    {
        await Task.Delay(2500);
        return $"(kb) background notes for: {arg}";
    }
}

/// <summary>Tiny recursive-descent arithmetic evaluator: + - * / ( ) and unary minus. No eval of arbitrary code.</summary>
public static class Calculator
{
    public static double Evaluate(string expression)
    {
        var p = new Parser(expression);
        var value = p.ParseExpression();
        p.ExpectEnd();
        return value;
    }

    private class Parser
    {
        private readonly string _s;
        private int _i;

        public Parser(string s) => _s = s;

        public void ExpectEnd()
        {
            Skip();
            if (_i < _s.Length) throw new FormatException($"unexpected '{_s[_i]}'");
        }

        public double ParseExpression()
        {
            var v = ParseTerm();
            while (true)
            {
                Skip();
                if (Eat('+')) v += ParseTerm();
                else if (Eat('-')) v -= ParseTerm();
                else return v;
            }
        }

        private double ParseTerm()
        {
            var v = ParseFactor();
            while (true)
            {
                Skip();
                if (Eat('*')) v *= ParseFactor();
                else if (Eat('/'))
                {
                    var d = ParseFactor();
                    if (d == 0) throw new DivideByZeroException("division by zero");
                    v /= d;
                }
                else return v;
            }
        }

        private double ParseFactor()
        {
            Skip();
            if (Eat('-')) return -ParseFactor();
            if (Eat('('))
            {
                var v = ParseExpression();
                Skip();
                if (!Eat(')')) throw new FormatException("missing ')'");
                return v;
            }
            var start = _i;
            while (_i < _s.Length && (char.IsDigit(_s[_i]) || _s[_i] == '.')) _i++;
            if (start == _i) throw new FormatException("expected a number");
            return double.Parse(_s[start.._i], CultureInfo.InvariantCulture);
        }

        private bool Eat(char c)
        {
            if (_i < _s.Length && _s[_i] == c) { _i++; return true; }
            return false;
        }

        private void Skip()
        {
            while (_i < _s.Length && char.IsWhiteSpace(_s[_i])) _i++;
        }
    }
}
