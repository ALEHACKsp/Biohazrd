﻿using ClangSharp;
using ClangSharp.Interop;
using System.Diagnostics;
using System.Runtime.InteropServices;
using static ClangSharp.Interop.CXTypeKind;
using ClangType = ClangSharp.Type;

namespace ClangSharpTest2020
{
    partial class TranslatedFile
    {
        public ClangType FindType(CXType typeHandle)
        {
            if (typeHandle.kind == CXTypeKind.CXType_Invalid)
            {
                Diagnostic(Severity.Warning, $"Someone tried to get the Type for an invalid type handle.");
                return null;
            }

            return TranslationUnit.GetOrCreate(typeHandle);
        }

        internal void ReduceType(ClangType type, CXCursor associatedCursor, TypeTranslationContext context, out ClangType reducedType, out int levelsOfIndirection)
        {
            reducedType = type;
            levelsOfIndirection = 0;

            // Walk the type up until we find the type we actually want to print
            // This also figures out how many levels of indirection the type has
            while (true)
            {
                switch (reducedType)
                {
                    // Elaborated types are namespace-qualified types like physx::PxU32 instead of just PxU32.
                    case ElaboratedType elaboratedType:
                        reducedType = elaboratedType.NamedType;
                        break;
                    // For now, we discard typedefs and translate the type they alias
                    case TypedefType typedefType:
                        reducedType = typedefType.CanonicalType;
                        break;
                    case PointerType pointerType:
                        reducedType = pointerType.PointeeType;
                        levelsOfIndirection++;
                        break;
                    case ReferenceType referenceType:
                        // Our test codebase doesn't have any types like this, and I'm not actually sure what it is.
                        // Complain if we find one so we can hopefully resolve the issue.
                        if (referenceType.Kind == CXTypeKind.CXType_RValueReference)
                        { Diagnostic(Severity.Warning, associatedCursor, "Found RValue reference type. This type may not be translated correctly (due to lack of real-world samples.)"); }

                        // References are translated as pointers
                        reducedType = referenceType.PointeeType;
                        levelsOfIndirection++;
                        break;
                    case ArrayType arrayType:
                        // Specific array type handling
                        switch (arrayType)
                        {
                            case ConstantArrayType constantArrayType:
                                if (context == TypeTranslationContext.ForReturn)
                                { Diagnostic(Severity.Error, associatedCursor, "Cannot translate constant-sized array return type."); }
                                else if (context == TypeTranslationContext.ForField)
                                // Don't reduce this type any further, these need special translation.
                                { return; }
                                else if (context == TypeTranslationContext.ForParameter)
                                { Diagnostic(Severity.Warning, associatedCursor, "The size of the array for this parameter won't be translated."); }
                                break;
                            case DependentSizedArrayType dependentSizedArrayType:
                                // Dependent-sized arrays are arrays sized by a template parameter
                                Diagnostic(Severity.Error, associatedCursor, "Dependent-sized arrays are not supported.");
                                break;
                            case IncompleteArrayType incompleteArrayType:
                                if (context != TypeTranslationContext.ForParameter)
                                { Diagnostic(Severity.Error, associatedCursor, "Incomplete array types are only supported as parameters."); }
                                break;
                            default:
                                Diagnostic(Severity.Error, associatedCursor, $"Don't know how to translate array type {reducedType.GetType().Name} ({reducedType.Kind})");
                                break;
                        }

                        // If we're in the context other than a field, translate the array as a pointer
                        if (context != TypeTranslationContext.ForField)
                        { levelsOfIndirection++; }

                        reducedType = arrayType.ElementType;
                        break;
                    default:
                        // If we got this far, we either encountered a type we can't deal with or we hit a type we can translate.
                        return;
                }
            }
        }

        internal void ReduceType(ClangType type, Cursor associatedCursor, TypeTranslationContext context, out ClangType reducedType, out int levelsOfIndirection)
            => ReduceType(type, associatedCursor.Handle, context, out reducedType, out levelsOfIndirection);

        internal void WriteType(CodeWriter writer, ClangType type, CXCursor associatedCursor, TypeTranslationContext context)
        {
            ClangType reducedType;
            int levelsOfIndirection;
            ReduceType(type, associatedCursor, context, out reducedType, out levelsOfIndirection);

            WriteReducedType(writer, reducedType, levelsOfIndirection, type, associatedCursor, context);
        }

        internal void WriteType(CodeWriter writer, ClangType type, Cursor associatedCursor, TypeTranslationContext context)
            => WriteType(writer, type, associatedCursor.Handle, context);

        internal void WriteReducedType(CodeWriter writer, ClangType type, int levelsOfIndirection, ClangType unreducedType, CXCursor associatedCursor, TypeTranslationContext context)
        {
            // Handle function pointers
            if (type.Kind == CXTypeKind.CXType_FunctionProto)
            {
                FunctionProtoType functionType = (FunctionProtoType)type;
                WriteFunctionType(writer, functionType, associatedCursor, context);
                return;
            }

            // Determine the type name for built-in types
            //TODO: Limit to what C# allows when context is ForEnumUnderlyingType
            //TODO: Support function pointers (CXType_FunctionProto)
            (string typeName, long cSharpTypeSize) = type.Kind switch
            {
                CXType_Void => ("void", 0),
                CXType_Bool => ("bool", sizeof(bool)),

                // Character types
                // We always translate `char` (without an explicit sign) as `byte` because in C this type ususally indicates a string and
                // .NET's Encoding utilities all work with bytes.
                // (Additionally, good developers will explicitly sign numeric 8-bit fields since char's signedness is undefined)
                CXType_Char_S => ("byte", sizeof(byte)), // char (with -fsigned-char)
                CXType_Char_U => ("byte", sizeof(byte)), // char (with -fno-signed-char)
                CXType_WChar => ("char", sizeof(char)), // wchar_t
                CXType_Char16 => ("char", sizeof(char)), // char16_t

                // Unsigned integer types
                CXType_UChar => ("byte", sizeof(byte)), // unsigned char / uint8_t
                CXType_UShort => ("ushort", sizeof(ushort)),
                CXType_UInt => ("uint", sizeof(uint)),
                CXType_ULong => ("uint", sizeof(uint)),
                CXType_ULongLong => ("ulong", sizeof(ulong)),

                // Signed integer types
                CXType_SChar => ("sbyte", sizeof(sbyte)), // signed char / int8_t
                CXType_Short => ("short", sizeof(short)),
                CXType_Int => ("int", sizeof(int)),
                CXType_Long => ("int", sizeof(int)),
                CXType_LongLong => ("long", sizeof(long)),

                // Floating point types
                CXType_Float => ("float", sizeof(float)),
                CXType_Double => ("double", sizeof(double)),

                // If we got this far, we don't know how to translate this type
                _ => (null, 0)
            };

            // Handle records and enums
            //TODO: Deal with namespaces and such
            if (type.Kind == CXType_Record || type.Kind == CXType_Enum)
            {
                Debug.Assert(typeName is null, "The type name should not be available at this point.");
                TagType tagType = (TagType)type;

                // Try to get the translated declaration for the type
                if (DeclarationLookup.TryGetValue(tagType.Decl, out TranslatedDeclaration declaration))
                {
                    typeName = CodeWriter.SanitizeIdentifier(declaration.TranslatedName);

                    if (declaration is TranslatedRecord record)
                    { cSharpTypeSize = record.Size; }
                    else if (declaration is TranslatedEnum translatedEnum)
                    {
                        cSharpTypeSize = 0; //TODO: TranslatedEnum should track/expose this, but it doesn't.

                        // If the enum is translated as loose constants, we translate the underlying integer type instead
                        if (translatedEnum.WillTranslateAsLooseConstants)
                        {
                            WriteType(writer, translatedEnum.EnumDeclaration.IntegerType, associatedCursor, context);
                            return;
                        }
                    }
                    else
                    {
                        Debug.Assert(false, "It's expected that we have either a record or an enum.");
                        cSharpTypeSize = 0;
                    }

                    // Fully qualify the name
                    IDeclarationContainer container = declaration.Parent;
                    while (container is TranslatedRecord recordContainer)
                    {
                        typeName = $"{CodeWriter.SanitizeIdentifier(recordContainer.TranslatedName)}.{typeName}";
                        container = recordContainer.Parent;
                    }
                }
                // Otherwise default to translating the name literally
                else
                {
                    //TODO: We want this case to be rare and/or non-existent.
                    // We need to be smarter about how we translate entire libraries for that to happen though since
                    // right now these types are typically records outside of the file we're directly processing.
                    typeName = CodeWriter.SanitizeIdentifier(tagType.Decl.Name);
                    cSharpTypeSize = 0;
                }
            }

            // If the size of the C# type is known, we try to sanity-check it
            // If the check fails, we erase the type name and let the substitute logic run below
            // (This check failing likely indicates a programming issue, so we assert too.)
            if (cSharpTypeSize != 0 && type.Handle.SizeOf != cSharpTypeSize)
            {
                Diagnostic(Severity.Error, $"sizeof({type}) is {type.Handle.SizeOf}, but the translated sizeof({typeName}) is {cSharpTypeSize}.");
                Debug.Assert(false, "This size check shouldn't fail.");
                typeName = null;
            }

            // If the typename is an empty string, it's an anonymous enum or record
            if (typeName is object && typeName.Length == 0)
            {
                Debug.Assert(type.Kind == CXType_Record || type.Kind == CXTypeKind.CXType_Enum, "Only records or enums are expected to be anonymous.");
                Diagnostic(Severity.Warning, associatedCursor, "Tried to translate anonymous type.");

                if (type is TagType tagType)
                { Diagnostic(Severity.Note, tagType.Decl, "The anonymous type was declared here."); }

                // Set the type name to null so that the fallback logic runs
                typeName = null;
            }

            // If the type isn't supported, we try to translate it as a primitive
            if (typeName is null)
            {
                string reducedTypeNote = ReferenceEquals(type, unreducedType) ? "" : $" (reduced from `{unreducedType}`)";
                string warningPrefix = $"Not sure how to translate `{type}`{reducedTypeNote}";

                // Pointers to unknown types are changed to void pointers
                if (levelsOfIndirection > 0)
                {
                    typeName = "void";
                    Diagnostic(Severity.Warning, associatedCursor, $"{warningPrefix}, translated as void pointer.");
                }
                // Otherwise we try to find a matching primitive
                else
                {
                    typeName = type.Handle.SizeOf switch
                    {
                        sizeof(byte) => "byte",
                        sizeof(short) => "short",
                        sizeof(int) => "int",
                        sizeof(long) => "long",
                        // Note: There's no reason to try and handle IntPtr here.
                        // Even ignoring the fact that it'll be handled by the int or long branch,
                        // we aren't dealing with pointers at this point so we don't want to translate anything as such.
                        _ => null
                    };

                    if (typeName is object)
                    { Diagnostic(Severity.Warning, associatedCursor, $"{warningPrefix}, translated as same-size C# primitive type `{typeName}`."); }
                    else
                    {
                        //TODO: This is reasonable for fields since we use explicit layouts, but it totally breaks calling conventions.
                        // We need a way to retroactively mark the member as [Obsolete] to preven it from being used.
                        typeName = "byte";
                        Diagnostic
                        (
                            context == TypeTranslationContext.ForField ? Severity.Warning : Severity.Error,
                            associatedCursor,
                            $"{warningPrefix}`, translated as a `byte` since it isn't the size of any C# primitive."
                        );
                    }
                }
            }

            // Write out the type
            // Note that we do not want to use SanatizeSymbol here, because typeName might be a built-in type keyword.
            writer.Write(typeName);

            for (int i = 0; i < levelsOfIndirection; i++)
            { writer.Write('*'); }
        }

        internal void WriteReducedType(CodeWriter writer, ClangType type, int levelsOfIndirection, ClangType unreducedType, Cursor associatedCursor, TypeTranslationContext context)
            => WriteReducedType(writer, type, levelsOfIndirection, unreducedType, associatedCursor.Handle, context);

        private void WriteFunctionType(CodeWriter writer, FunctionProtoType functionType, CXCursor associatedCursor, TypeTranslationContext context)
        {
            // Get the calling convention
            string errorMessage;
            CallingConvention callingConvention = functionType.CallConv.GetCSharpCallingConvention(out errorMessage);

            if (errorMessage is object)
            {
                Diagnostic(Severity.Warning, associatedCursor, $"Error while translating function pointer type: {errorMessage}");
                writer.Write("void*");
                return;
            }

            // Write out the type
            writer.Write($"delegate* {callingConvention.ToString().ToLowerInvariant()}<");

            foreach (ClangType parameterType in functionType.ParamTypes)
            {
                WriteType(writer, parameterType, associatedCursor, TypeTranslationContext.ForParameter);
                writer.Write(", ");
            }

            WriteType(writer, functionType.ReturnType, associatedCursor, TypeTranslationContext.ForReturn);

            writer.Write('>');
        }
    }
}