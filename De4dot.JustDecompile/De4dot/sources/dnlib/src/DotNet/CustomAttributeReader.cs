/*
    Copyright (C) 2012-2014 de4dot@gmail.com

    Permission is hereby granted, free of charge, to any person obtaining
    a copy of this software and associated documentation files (the
    "Software"), to deal in the Software without restriction, including
    without limitation the rights to use, copy, modify, merge, publish,
    distribute, sublicense, and/or sell copies of the Software, and to
    permit persons to whom the Software is furnished to do so, subject to
    the following conditions:

    The above copyright notice and this permission notice shall be
    included in all copies or substantial portions of the Software.

    THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND,
    EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF
    MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT.
    IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY
    CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT,
    TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE
    SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
*/

﻿using System;
using System.Collections.Generic;
using System.IO;
using dnlib.IO;
using dnlib.Threading;

namespace dnlib.DotNet {
	/// <summary>
	/// Searches for a type according to custom attribute search rules: first try the
	/// current assembly, and if that fails, try mscorlib
	/// </summary>
	sealed class CAAssemblyRefFinder : IAssemblyRefFinder {
		readonly ModuleDef module;

		/// <summary>
		/// Constructor
		/// </summary>
		/// <param name="module">The module to search first</param>
		public CAAssemblyRefFinder(ModuleDef module) {
			this.module = module;
		}

		/// <inheritdoc/>
		public AssemblyRef FindAssemblyRef(TypeRef nonNestedTypeRef) {
			var modAsm = module.Assembly;
			if (modAsm != null) {
				var type = modAsm.Find(nonNestedTypeRef);
				if (type != null)
					return module.UpdateRowId(new AssemblyRefUser(modAsm));
			}
			else if (module.Find(nonNestedTypeRef) != null)
				return AssemblyRef.CurrentAssembly;

			var corLibAsm = module.Context.AssemblyResolver.Resolve(module.CorLibTypes.AssemblyRef, module);
			if (corLibAsm != null) {
				var type = corLibAsm.Find(nonNestedTypeRef);
				if (type != null)
					return module.CorLibTypes.AssemblyRef;
			}

			if (modAsm != null)
				return module.UpdateRowId(new AssemblyRefUser(modAsm));
			return AssemblyRef.CurrentAssembly;
		}
	}

	/// <summary>
	/// Thrown by CustomAttributeReader when it fails to parse a custom attribute blob
	/// </summary>
	[Serializable]
	class CABlobParserException : Exception {
		/// <summary>
		/// Default constructor
		/// </summary>
		public CABlobParserException() {
		}

		/// <summary>
		/// Constructor
		/// </summary>
		/// <param name="message">Error message</param>
		public CABlobParserException(string message)
			: base(message) {
		}

		/// <summary>
		/// Constructor
		/// </summary>
		/// <param name="message">Error message</param>
		/// <param name="innerException">Other exception</param>
		public CABlobParserException(string message, Exception innerException)
			: base(message, innerException) {
		}
	}

	/// <summary>
	/// Reads custom attributes from the #Blob stream
	/// </summary>
	public struct CustomAttributeReader : IDisposable {
		readonly ModuleDef module;
		readonly IImageStream reader;
		readonly ICustomAttributeType ctor;
		GenericArguments genericArguments;
		RecursionCounter recursionCounter;
		bool verifyReadAllBytes;
		readonly bool ownReader;

		/// <summary>
		/// Reads a custom attribute
		/// </summary>
		/// <param name="readerModule">Reader module</param>
		/// <param name="ctor">Custom attribute constructor</param>
		/// <param name="offset">Offset of custom attribute in the #Blob stream</param>
		/// <returns>A new <see cref="CustomAttribute"/> instance</returns>
		public static CustomAttribute Read(ModuleDefMD readerModule, ICustomAttributeType ctor, uint offset) {
			if (ctor == null)
				return CreateEmpty(ctor);
			using (var reader = new CustomAttributeReader(readerModule, ctor, offset)) {
				try {
					return reader.Read();
				}
				catch (CABlobParserException) {
					return new CustomAttribute(ctor, reader.GetRawBlob());
				}
				catch (IOException) {
					return new CustomAttribute(ctor, reader.GetRawBlob());
				}
			}
		}

		/// <summary>
		/// Reads a custom attribute
		/// </summary>
		/// <param name="module">Owner module</param>
		/// <param name="stream">A stream positioned at the the first byte of the CA blob</param>
		/// <param name="ctor">Custom attribute constructor</param>
		/// <returns>A new <see cref="CustomAttribute"/> instance or <c>null</c> if one of the
		/// args is <c>null</c> or if we failed to parse the CA blob</returns>
		public static CustomAttribute Read(ModuleDef module, IImageStream stream, ICustomAttributeType ctor) {
			if (stream == null || ctor == null)
				return null;
			try {
				using (var reader = new CustomAttributeReader(module, stream, ctor))
					return reader.Read();
			}
			catch (CABlobParserException) {
				return null;
			}
			catch (IOException) {
				return null;
			}
		}

		static CustomAttribute CreateEmpty(ICustomAttributeType ctor) {
			return new CustomAttribute(ctor, new byte[0]);
		}

		CustomAttributeReader(ModuleDefMD readerModule, ICustomAttributeType ctor, uint offset) {
			this.module = readerModule;
			this.reader = readerModule.BlobStream.CreateStream(offset);
			this.ownReader = true;
			this.ctor = ctor;
			this.genericArguments = null;
			this.recursionCounter = new RecursionCounter();
			this.verifyReadAllBytes = false;
		}

		CustomAttributeReader(ModuleDef module, IImageStream reader, ICustomAttributeType ctor) {
			this.module = module;
			this.reader = reader;
			this.ownReader = false;
			this.ctor = ctor;
			this.genericArguments = null;
			this.recursionCounter = new RecursionCounter();
			this.verifyReadAllBytes = false;
		}

		byte[] GetRawBlob() {
			return reader.ReadAllBytes();
		}

		CustomAttribute Read() {
			var methodSig = ctor == null ? null : ((IMethodDefOrRef)ctor).MethodSig;
			if (methodSig == null)
				throw new CABlobParserException("ctor is null or not a method");

			var mrCtor = ctor as MemberRef;
			if (mrCtor != null) {
				var owner = mrCtor.Class as TypeSpec;
				if (owner != null) {
					var gis = owner.TypeSig as GenericInstSig;
					if (gis != null) {
						genericArguments = new GenericArguments();
						genericArguments.PushTypeArgs(gis.GenericArguments);
					}
				}
			}

			bool isEmpty = methodSig.Params.Count == 0 && reader.Position == reader.Length;
			if (!isEmpty && reader.ReadUInt16() != 1)
				throw new CABlobParserException("Invalid CA blob prolog");

			var ctorArgs = new List<CAArgument>(methodSig.Params.Count);
			foreach (var arg in methodSig.Params.GetSafeEnumerable())
				ctorArgs.Add(ReadFixedArg(FixTypeSig(arg)));

			// Some tools don't write the next ushort if there are no named arguments.
			int numNamedArgs = reader.Position == reader.Length ? 0 : reader.ReadUInt16();
			var namedArgs = new List<CANamedArgument>(numNamedArgs);
			for (int i = 0; i < numNamedArgs; i++)
				namedArgs.Add(ReadNamedArgument());

			// verifyReadAllBytes will be set when we guess the underlying type of an enum.
			// To make sure we guessed right, verify that we read all bytes.
			if (verifyReadAllBytes && reader.Position != reader.Length)
				throw new CABlobParserException("Not all CA blob bytes were read");

			return new CustomAttribute(ctor, ctorArgs, namedArgs);
		}

		TypeSig FixTypeSig(TypeSig type) {
			return SubstituteGenericParameter(type.RemoveModifiers()).RemoveModifiers();
		}

		TypeSig SubstituteGenericParameter(TypeSig type) {
			if (genericArguments == null)
				return type;
			return genericArguments.Resolve(type);
		}

		CAArgument ReadFixedArg(TypeSig argType) {
			if (!recursionCounter.Increment())
				throw new CABlobParserException("Too much recursion");
			if (argType == null)
				throw new CABlobParserException("null argType");
			CAArgument result;

			var arrayType = argType as SZArraySig;
			if (arrayType != null)
				result = ReadArrayArgument(arrayType);
			else
				result = ReadElem(argType);

			recursionCounter.Decrement();
			return result;
		}

		CAArgument ReadElem(TypeSig argType) {
			if (argType == null)
				throw new CABlobParserException("null argType");
			TypeSig realArgType;
			var value = ReadValue((SerializationType)argType.ElementType, argType, out realArgType);
			if (realArgType == null)
				throw new CABlobParserException("Invalid arg type");

			// One example when this is true is when prop/field type is object and
			// value type is string[]
			if (value is CAArgument)
				return (CAArgument)value;

			return new CAArgument(realArgType, value);
		}

		object ReadValue(SerializationType etype, TypeSig argType, out TypeSig realArgType) {
			if (!recursionCounter.Increment())
				throw new CABlobParserException("Too much recursion");

			object result;
			switch (etype) {
			case SerializationType.Boolean:
				realArgType = module.CorLibTypes.Boolean;
				result = reader.ReadByte() != 0;
				break;

			case SerializationType.Char:
				realArgType = module.CorLibTypes.Char;
				result = (char)reader.ReadUInt16();
				break;

			case SerializationType.I1:
				realArgType = module.CorLibTypes.SByte;
				result = reader.ReadSByte();
				break;

			case SerializationType.U1:
				realArgType = module.CorLibTypes.Byte;
				result = reader.ReadByte();
				break;

			case SerializationType.I2:
				realArgType = module.CorLibTypes.Int16;
				result = reader.ReadInt16();
				break;

			case SerializationType.U2:
				realArgType = module.CorLibTypes.UInt16;
				result = reader.ReadUInt16();
				break;

			case SerializationType.I4:
				realArgType = module.CorLibTypes.Int32;
				result = reader.ReadInt32();
				break;

			case SerializationType.U4:
				realArgType = module.CorLibTypes.UInt32;
				result = reader.ReadUInt32();
				break;

			case SerializationType.I8:
				realArgType = module.CorLibTypes.Int64;
				result = reader.ReadInt64();
				break;

			case SerializationType.U8:
				realArgType = module.CorLibTypes.UInt64;
				result = reader.ReadUInt64();
				break;

			case SerializationType.R4:
				realArgType = module.CorLibTypes.Single;
				result = reader.ReadSingle();
				break;

			case SerializationType.R8:
				realArgType = module.CorLibTypes.Double;
				result = reader.ReadDouble();
				break;

			case SerializationType.String:
				realArgType = module.CorLibTypes.String;
				result = ReadUTF8String();
				break;

			// It's ET.ValueType if it's eg. a ctor enum arg type
			case (SerializationType)ElementType.ValueType:
				if (argType == null)
					throw new CABlobParserException("Invalid element type");
				realArgType = argType;
				result = ReadEnumValue(GetEnumUnderlyingType(argType));
				break;

			// It's ET.Object if it's a ctor object arg type
			case (SerializationType)ElementType.Object:
			case SerializationType.TaggedObject:
				realArgType = ReadFieldOrPropType();
				var arraySig = realArgType as SZArraySig;
				if (arraySig != null)
					result = ReadArrayArgument(arraySig);
				else {
					TypeSig tmpType;
					result = ReadValue((SerializationType)realArgType.ElementType, realArgType, out tmpType);
				}
				break;

			// It's ET.Class if it's eg. a ctor System.Type arg type
			case (SerializationType)ElementType.Class:
				var tdr = argType as TypeDefOrRefSig;
				if (tdr != null && tdr.DefinitionAssembly.IsCorLib() && tdr.Namespace == "System") {
					if (tdr.TypeName == "Type") {
						result = ReadValue(SerializationType.Type, tdr, out realArgType);
						break;
					}
					if (tdr.TypeName == "String") {
						result = ReadValue(SerializationType.String, tdr, out realArgType);
						break;
					}
					if (tdr.TypeName == "Object") {
						result = ReadValue(SerializationType.TaggedObject, tdr, out realArgType);
						break;
					}
				}

				// Assume it's an enum that couldn't be resolved
				realArgType = argType;
				return ReadEnumValue(null);

			case SerializationType.Type:
				realArgType = argType;
				result = ReadType();
				break;

			case SerializationType.Enum:
				realArgType = ReadType();
				result = ReadEnumValue(GetEnumUnderlyingType(realArgType));
				break;

			default:
				throw new CABlobParserException("Invalid element type");
			}

			recursionCounter.Decrement();
			return result;
		}

		object ReadEnumValue(TypeSig underlyingType) {
			if (underlyingType != null) {
				if (underlyingType.ElementType < ElementType.Boolean || underlyingType.ElementType > ElementType.U8)
					throw new CABlobParserException("Invalid enum underlying type");
				TypeSig realArgType;
				return ReadValue((SerializationType)underlyingType.ElementType, underlyingType, out realArgType);
			}

			// We couldn't resolve the type ref. It should be an enum, but we don't know for sure.
			// Most enums use Int32 as the underlying type. Assume that's true also in this case.
			// Since we're guessing, verify that we've read all CA blob bytes. If we haven't, then
			// we probably guessed wrong.
			verifyReadAllBytes = true;
			return reader.ReadInt32();
		}

		TypeSig ReadType() {
			var name = ReadUTF8String();
			var asmRefFinder = new CAAssemblyRefFinder(module);
			var type = TypeNameParser.ParseAsTypeSigReflection(module, UTF8String.ToSystemStringOrEmpty(name), asmRefFinder);
			if (type == null)
				throw new CABlobParserException("Could not parse type");
			return type;
		}

		/// <summary>
		/// Gets the enum's underlying type
		/// </summary>
		/// <param name="type">An enum type</param>
		/// <returns>The underlying type or <c>null</c> if we couldn't resolve the type ref</returns>
		/// <exception cref="CABlobParserException">If <paramref name="type"/> is not an enum or <c>null</c></exception>
		static TypeSig GetEnumUnderlyingType(TypeSig type) {
			if (type == null)
				throw new CABlobParserException("null enum type");
			var td = GetTypeDef(type);
			if (td == null)
				return null;
			if (!td.IsEnum)
				throw new CABlobParserException("Not an enum");
			return td.GetEnumUnderlyingType().RemoveModifiers();
		}

		/// <summary>
		/// Converts <paramref name="type"/> to a <see cref="TypeDef"/>, possibly resolving
		/// a <see cref="TypeRef"/>
		/// </summary>
		/// <param name="type">The type</param>
		/// <returns>A <see cref="TypeDef"/> or <c>null</c> if we couldn't resolve the
		/// <see cref="TypeRef"/> or if <paramref name="type"/> is a type spec</returns>
		static TypeDef GetTypeDef(TypeSig type) {
			var tdr = type as TypeDefOrRefSig;
			if (tdr != null) {
				var td = tdr.TypeDef;
				if (td != null)
					return td;

				var tr = tdr.TypeRef;
				if (tr != null)
					return tr.Resolve();
			}

			return null;
		}

		CAArgument ReadArrayArgument(SZArraySig arrayType) {
			if (!recursionCounter.Increment())
				throw new CABlobParserException("Too much recursion");
			var arg = new CAArgument(arrayType);

			int arrayCount = reader.ReadInt32();
			if (arrayCount == -1) {	// -1 if it's null
			}
			else if (arrayCount < 0)
				throw new CABlobParserException("Array is too big");
			else {
				var array = ThreadSafeListCreator.Create<CAArgument>(arrayCount);
				arg.Value = array;
				for (int i = 0; i < arrayCount; i++)
					array.Add(ReadFixedArg(FixTypeSig(arrayType.Next)));
			}

			recursionCounter.Decrement();
			return arg;
		}

		CANamedArgument ReadNamedArgument() {
			bool isField;
			switch ((SerializationType)reader.ReadByte()) {
			case SerializationType.Property:isField = false; break;
			case SerializationType.Field:	isField = true; break;
			default: throw new CABlobParserException("Named argument is not a field/property");
			}

			TypeSig fieldPropType = ReadFieldOrPropType();
			var name = ReadUTF8String();
			var argument = ReadFixedArg(fieldPropType);

			return new CANamedArgument(isField, fieldPropType, name, argument);
		}

		TypeSig ReadFieldOrPropType() {
			if (!recursionCounter.Increment())
				throw new CABlobParserException("Too much recursion");
			TypeSig result;
			switch ((SerializationType)reader.ReadByte()) {
			case SerializationType.Boolean: result = module.CorLibTypes.Boolean; break;
			case SerializationType.Char:	result = module.CorLibTypes.Char; break;
			case SerializationType.I1:		result = module.CorLibTypes.SByte; break;
			case SerializationType.U1:		result = module.CorLibTypes.Byte; break;
			case SerializationType.I2:		result = module.CorLibTypes.Int16; break;
			case SerializationType.U2:		result = module.CorLibTypes.UInt16; break;
			case SerializationType.I4:		result = module.CorLibTypes.Int32; break;
			case SerializationType.U4:		result = module.CorLibTypes.UInt32; break;
			case SerializationType.I8:		result = module.CorLibTypes.Int64; break;
			case SerializationType.U8:		result = module.CorLibTypes.UInt64; break;
			case SerializationType.R4:		result = module.CorLibTypes.Single; break;
			case SerializationType.R8:		result = module.CorLibTypes.Double; break;
			case SerializationType.String:	result = module.CorLibTypes.String; break;
			case SerializationType.SZArray: result = new SZArraySig(ReadFieldOrPropType()); break;
			case SerializationType.Type:	result = new ClassSig(module.CorLibTypes.GetTypeRef("System", "Type")); break;
			case SerializationType.TaggedObject: result = module.CorLibTypes.Object; break;
			case SerializationType.Enum:	result = ReadType(); break;
			default: throw new CABlobParserException("Invalid type");
			}
			recursionCounter.Decrement();
			return result;
		}

		UTF8String ReadUTF8String() {
			if (reader.ReadByte() == 0xFF)
				return null;
			reader.Position--;
			uint len;
			if (!reader.ReadCompressedUInt32(out len))
				throw new CABlobParserException("Could not read compressed UInt32");
			if (len == 0)
				return UTF8String.Empty;
			return new UTF8String(reader.ReadBytes((int)len));
		}

		/// <inheritdoc/>
		public void Dispose() {
			if (ownReader && reader != null)
				reader.Dispose();
		}
	}
}
