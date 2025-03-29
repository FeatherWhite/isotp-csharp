using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;

namespace IsoTpLibrary
{
    [AttributeUsage(AttributeTargets.Field)]
    public class ByteMappingAttribute : Attribute
    {
        public int BitOffset { get; }
        public int BitLength { get; }

        public ByteMappingAttribute(int bitOffset, int bitLength)
        {
            BitOffset = bitOffset;
            BitLength = bitLength;
        }
    }

    public class StructMapping
    {
        public static T BytesToStruct<T>(byte[] data) where T : struct
        {

            T result = new T();
            Type type = typeof(T);
            FieldInfo[] fields = type.GetFields(BindingFlags.Public | BindingFlags.Instance);
            object obj = result;
            int byteIndex = 0;
            foreach (var field in fields)
            {
                var attribute = field.GetCustomAttribute<ByteMappingAttribute>();
                if (attribute != null)
                {
                    byteIndex = attribute.BitOffset / 8;
                    int bitOffset = attribute.BitOffset % 8;
                    int bitLength = attribute.BitLength;

                    // Extract the bits from the byte array
                    byte value = (byte)((data[byteIndex] >> bitOffset) & ((1 << bitLength) - 1));
                    field.SetValue(obj, value);
                }
                else
                {
                    if(field.FieldType == typeof(byte))
                    {
                        field.SetValue(obj, data[byteIndex]);
                    }
                    if(field.FieldType == typeof(byte[]))
                    {
                        byte[] values = new byte[data.Length - byteIndex];
                        if(values != null)
                        {
                            for (int i = 0; i < values.Length; i++)
                            {
                                values[i] = data[byteIndex++];
                            }
                            field.SetValue(obj, values);
                        }
                        byteIndex--;
                    }
                }
                byteIndex++;
            }

            return (T)obj;
        }

        public static byte[] StructToBytes<T>(T obj) where T : struct
        {
            Type type = typeof(T);
            FieldInfo[] fields = type.GetFields(BindingFlags.Public | BindingFlags.Instance);
            byte[] data = new byte[8]; // Assuming we are still working with 8 bytes
            int byteIndex = 0;
            foreach (var field in fields)
            {
                var attribute = field.GetCustomAttribute<ByteMappingAttribute>();
                if (attribute != null)
                {
                    byteIndex = attribute.BitOffset / 8;
                    int bitOffset = attribute.BitOffset % 8;
                    int bitLength = attribute.BitLength;

                    // Get the value of the field
                    byte value = (byte)field.GetValue(obj);

                    // Clear the bits in the target byte
                    data[byteIndex] &= (byte)~(((1 << bitLength) - 1) << bitOffset);

                    // Set the bits in the target byte
                    data[byteIndex] |= (byte)((value & ((1 << bitLength) - 1)) << bitOffset);
                }
                else
                {
                    if(field.FieldType == typeof(byte))
                    {
                        data[byteIndex] = (byte)field.GetValue(obj);
                    }
                    if (field.FieldType == typeof(byte[]))
                    {
                        byte[] values = (byte[])field.GetValue(obj);
                        if(values != null)
                        {
                            foreach (var value in values)
                            {
                                data[byteIndex] = value;
                                byteIndex++;
                            }
                            byteIndex--;
                        }
                    }
                }
                byteIndex++;
            }

            return data;
        }
    }

}
