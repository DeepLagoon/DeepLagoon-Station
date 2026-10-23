using System.Security.Cryptography;

namespace Content.Server._DeepLagoon.Apartments;

public sealed class ApartmentRoundNumbers
{
    private readonly Dictionary<Guid, int> _numbers = new();
    private readonly Dictionary<int, Guid> _owners = new();
    private readonly HashSet<int> _retired = new();
    private HashSet<int> _previous = new();

    public int Number(Guid owner)
    {
        if (_numbers.TryGetValue(owner, out var existing)) return existing;
        int number;
        do number = RandomNumberGenerator.GetInt32(10_000_000, 100_000_000);
        while (_owners.ContainsKey(number) || _previous.Contains(number));
        _numbers.Add(owner, number);
        _owners.Add(number, owner);
        return number;
    }

    public int Rotate(Guid owner)
    {
        if (_numbers.Remove(owner, out var old)) { _owners.Remove(old); _previous.Add(old); _retired.Add(old); }
        return Number(owner);
    }

    public Guid? Owner(int number) => _owners.TryGetValue(number, out var owner) ? owner : null;

    public void Reset()
    {
        if (_owners.Count > 0 || _retired.Count > 0) { _previous = new HashSet<int>(_owners.Keys); _previous.UnionWith(_retired); }
        _retired.Clear();
        _numbers.Clear();
        _owners.Clear();
    }
}
