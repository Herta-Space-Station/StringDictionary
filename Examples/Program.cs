using System;
using Cysharp.Text;
using Herta;

namespace Examples
{
    internal sealed class Program
    {
        private static void Main()
        {
            var dictionary = new StringDictionary<TestResource>();
            Example(dictionary);
        }

        public static void Example(StringDictionary<TestResource> dictionary)
        {
            using var stringBuilder = ZString.CreateStringBuilder();
            stringBuilder.Append(100);
            ref var resource = ref dictionary.GetValueRefOrAddDefault(stringBuilder.AsSpan(), out var exists);
            Console.WriteLine(resource == null);
            if (!exists)
                resource = new TestResource { Value = 100 };

            Console.WriteLine(resource!.Value);

            //TODO: never use `ToString()`

            Console.WriteLine(dictionary[stringBuilder.ToString()].Value);
        }
    }

    public sealed class TestResource
    {
        public int Value;
    }
}