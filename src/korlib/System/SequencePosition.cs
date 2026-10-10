// NeutrinoOS korlib - SequencePosition (Kestrel port milestone M2)
//
// NOTE: the BCL declares SequencePosition in the ROOT System namespace
// (not System.Buffers). JITTest/DDK code compiled against the BCL emits
// MemberRefs that name System.SequencePosition, so korlib must declare it
// exactly there or on-device MemberRef resolution fails with a signature
// mismatch ("type not found in target asm").

namespace System
{
    public readonly struct SequencePosition : IEquatable<SequencePosition>
    {
        private readonly object? _object;
        private readonly int _integer;

        public SequencePosition(object? @object, int integer)
        {
            _object = @object;
            _integer = integer;
        }

        public object? GetObject() => _object;

        public int GetInteger() => _integer;

        public bool Equals(SequencePosition other) =>
            _integer == other._integer && ReferenceEquals(_object, other._object);

        public override bool Equals(object? obj) => obj is SequencePosition other && Equals(other);

        public override int GetHashCode() => (_object == null ? 0 : _object.GetHashCode()) ^ _integer;

        public static bool operator ==(SequencePosition left, SequencePosition right) => left.Equals(right);
        public static bool operator !=(SequencePosition left, SequencePosition right) => !left.Equals(right);
    }
}
