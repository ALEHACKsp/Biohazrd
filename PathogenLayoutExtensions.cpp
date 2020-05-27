//===----------------------------------------------------------------------===//
//
// Pathogen Studios extensions to libclang: Layout info extensions
// Provides functions for reading the memory and vtable layout of a type
//
// Useful references:
// * lib/AST/RecordLayoutBuilder.cpp (Used for -fdump-record-layouts)
// * lib/AST/VTableBuilder.cpp       (Used for -fdump-vtable-layouts)
//
//===----------------------------------------------------------------------===//
// clang-format off

#include "CIndexer.h"
#include "CXCursor.h"
#include "CXString.h"
#include "CXType.h"
#include "clang/AST/ASTContext.h"
#include "clang/AST/RecordLayout.h"
#include "clang/AST/Type.h"
#include "clang/AST/VTableBuilder.h"

#include <memory>

using namespace clang;

#define PATHOGEN_EXPORT extern "C" CINDEX_LINKAGE

typedef unsigned char interop_bool;

enum class PathogenRecordFieldKind : int32_t
{
    Normal,
    VTablePtr,
    NonVirtualBase,
    VirtualBaseTablePtr, //!< Only appears in Microsoft ABI
    VTorDisp, //!< Only appears in Microsoft ABI
    VirtualBase,
};

struct PathogenRecordField
{
    PathogenRecordFieldKind Kind;
    int64_t Offset;
    PathogenRecordField* NextField;
    CXString Name;

    //! When Kind == Normal, this is the type of the field
    //! When Kind == NonVirtualBase, VTorDisp, or VirtualBase, this is the type of the base
    //! When Kind == VTablePtr, this is void**
    //! When Kind == VirtualBaseTablePtr, this is void*
    CXType Type;

    // Only relevant when Kind == Normal
    CXCursor FieldDeclaration;
    interop_bool IsBitField;

    // Only relevant when IsBitField == true
    unsigned int BitFieldStart;
    unsigned int BitFieldWidth;

    // Only relevant when Kind == NonVirtualBase or VirtialBase
    interop_bool IsPrimaryBase;
};

enum class PathogenVTableEntryKind : int32_t
{
    VCallOffset,
    VBaseOffset,
    OffsetToTop,
    RTTI,
    FunctionPointer,
    CompleteDestructorPointer,
    DeletingDestructorPointer,
    UnusedFunctionPointer,
};

// We verify the enums match manually because we need a stable definition here to reflect on the C# side of things.
#define verify_vtable_entry_kind(PATHOGEN_KIND, CLANG_KIND) static_assert((int)(PathogenVTableEntryKind:: ## PATHOGEN_KIND) == (int)(VTableComponent:: ## CLANG_KIND), #PATHOGEN_KIND " must match " #CLANG_KIND);
verify_vtable_entry_kind(VCallOffset, CK_VCallOffset)
verify_vtable_entry_kind(VBaseOffset, CK_VBaseOffset)
verify_vtable_entry_kind(OffsetToTop, CK_OffsetToTop)
verify_vtable_entry_kind(RTTI, CK_RTTI)
verify_vtable_entry_kind(FunctionPointer, CK_FunctionPointer)
verify_vtable_entry_kind(CompleteDestructorPointer, CK_CompleteDtorPointer)
verify_vtable_entry_kind(DeletingDestructorPointer, CK_DeletingDtorPointer)
verify_vtable_entry_kind(UnusedFunctionPointer, CK_UnusedFunctionPointer)

//TODO: It'd be nice to know which entry of the table corresponds with a vtable pointer in the associated record.
// Unfortunately this is non-trivial to get. For simple inheritance trees with no multi-inheritance this should simply the first entry after the RTTI pointer.
// Clang will dump this with -fdump-vtable-layouts on Itanium platforms. Ctrl+F for "vtable address --" in VTableBuilder.cpp
// This is also hard to model with the way we present record layouts since bases are referenced rather than embedded.
struct PathogenVTableEntry
{
    PathogenVTableEntryKind Kind;

    //! Only relevant when Kind == FunctionPointer, CompleteDestructorPointer, DeletingDestructorPointer, or UnusedFunctionPointer
    CXCursor MethodDeclaration;

    //! Only relevant when Kind == RTTI
    CXCursor RttiType;

    //! Only relevant when Kind == VCallOffset, VBaseOffset, or OffsetToTop
    int64_t Offset;

    PathogenVTableEntry(CXTranslationUnit translationUnit, const VTableComponent& component)
    {
        Kind = (PathogenVTableEntryKind)component.getKind();
        MethodDeclaration = {};
        RttiType = {};
        Offset = 0;
        
        switch (Kind)
        {
            case PathogenVTableEntryKind::VCallOffset:
                Offset = component.getVCallOffset().getQuantity();
                break;
            case PathogenVTableEntryKind::VBaseOffset:
                Offset = component.getVBaseOffset().getQuantity();
                break;
            case PathogenVTableEntryKind::OffsetToTop:
                Offset = component.getOffsetToTop().getQuantity();
                break;
            case PathogenVTableEntryKind::RTTI:
                RttiType = cxcursor::MakeCXCursor(component.getRTTIDecl(), translationUnit);
                break;
            case PathogenVTableEntryKind::FunctionPointer:
            case PathogenVTableEntryKind::CompleteDestructorPointer:
            case PathogenVTableEntryKind::DeletingDestructorPointer:
            case PathogenVTableEntryKind::UnusedFunctionPointer:
                MethodDeclaration = cxcursor::MakeCXCursor(component.getFunctionDecl(), translationUnit);
                break;
        }
    }
};

struct PathogenVTable
{
    int32_t EntryCount;
    PathogenVTableEntry* Entries;
    //! Only relevant on Microsoft ABI
    PathogenVTable* NextVTable;

    PathogenVTable(CXTranslationUnit translationUnit, const VTableLayout& layout)
    {
        NextVTable = nullptr;

        ArrayRef<VTableComponent> components = layout.vtable_components();
        EntryCount = (int32_t)components.size();
        Entries = (PathogenVTableEntry*)malloc(sizeof(PathogenVTableEntry) * EntryCount);

        for (int32_t i = 0; i < EntryCount; i++)
        {
            Entries[i] = PathogenVTableEntry(translationUnit, components[i]);
        }
    }

    ~PathogenVTable()
    {
        free(Entries);
    }
};

struct PathogenRecordLayout
{
    PathogenRecordField* FirstField;
    PathogenVTable* FirstVTable;

    int64_t Size;
    int64_t Alignment;

    // For C++ records only
    interop_bool IsCppRecord;
    int64_t NonVirtualSize;
    int64_t NonVirtualAlignment;

    PathogenRecordField* AddField(PathogenRecordFieldKind kind, int64_t offset, CXString name, CXType type)
    {
        // Find the insertion point for the field
        PathogenRecordField** insertPoint = &FirstField;

        while (*insertPoint != nullptr && (*insertPoint)->Offset <= offset)
        { insertPoint = &((*insertPoint)->NextField); }

        // Insert the new field
        PathogenRecordField* field = new PathogenRecordField();

        field->Kind = kind;
        field->Offset = offset;
        field->Name = name;
        field->Type = type;

        field->NextField = *insertPoint;
        *insertPoint = field;

        return field;
    }

    PathogenRecordField* AddField(PathogenRecordFieldKind kind, int64_t offset, CXTranslationUnit translationUnit, const FieldDecl& field)
    {
        CXType type = cxtype::MakeCXType(field.getType(), translationUnit);
        PathogenRecordField* ret = AddField(kind, offset, cxstring::createDup(field.getName()), type);
        ret->FieldDeclaration = cxcursor::MakeCXCursor(&field, translationUnit);
        return ret;
    }

    PathogenVTable* AddVTableLayout(CXTranslationUnit translationUnit, const VTableLayout& layout)
    {
        // Find insertion point for the new table
        PathogenVTable** insertPoint = &FirstVTable;

        while (*insertPoint != nullptr)
        { insertPoint = &((*insertPoint)->NextVTable); }

        // Insert the new table
        PathogenVTable* vTable = new PathogenVTable(translationUnit, layout);

        vTable->NextVTable = *insertPoint;
        *insertPoint = vTable;

        return vTable;
    }

    ~PathogenRecordLayout()
    {
        // Delete all fields
        for (PathogenRecordField* field = FirstField; field;)
        {
            PathogenRecordField* nextField = field->NextField;
            clang_disposeString(field->Name);
            delete field;
            field = nextField;
        }

        // Delete all VTables
        for (PathogenVTable* vTable = FirstVTable; vTable;)
        {
            PathogenVTable* nextVTable = vTable->NextVTable;
            delete vTable;
            vTable = nextVTable;
        }
    }
};

static bool IsMsLayout(const ASTContext& context)
{
    return context.getTargetInfo().getCXXABI().isMicrosoft();
}

PATHOGEN_EXPORT PathogenRecordLayout* pathogen_GetRecordLayout(CXCursor cursor)
{
    // The cursor must be a declaration
    if (!clang_isDeclaration(cursor.kind))
    {
        return nullptr;
    }

    // Get the record declaration
    const Decl* declaration = cxcursor::getCursorDecl(cursor);
    const RecordDecl* record = dyn_cast_or_null<RecordDecl>(declaration);

    // The cursor must be a record declaration
    if (record == nullptr)
    {
        return nullptr;
    }

    // The cursor must have a definition (IE: it can't be a forward-declaration.)
    if (record->getDefinition() == nullptr)
    {
        return nullptr;
    }

    // Get the AST context
    ASTContext& context = cxcursor::getCursorContext(cursor);

    // Get the translation unit
    CXTranslationUnit translationUnit = clang_Cursor_getTranslationUnit(cursor);

    // Get the void* and void** types
    CXType voidPointerType = cxtype::MakeCXType(context.VoidPtrTy, translationUnit);
    CXType voidPointerPointerType = cxtype::MakeCXType(context.getPointerType(context.VoidPtrTy), translationUnit);

    // Get the record layout
    const ASTRecordLayout& layout = context.getASTRecordLayout(record);

    // Get the C++ record if applicable
    const CXXRecordDecl* cxxRecord = dyn_cast<CXXRecordDecl>(record);

    // Create the record layout
    PathogenRecordLayout* ret = new PathogenRecordLayout();
    ret->Size = layout.getSize().getQuantity();
    ret->Alignment = layout.getAlignment().getQuantity();
    
    if (cxxRecord)
    {
        ret->IsCppRecord = true;
        ret->NonVirtualSize = layout.getNonVirtualSize().getQuantity();
        ret->NonVirtualAlignment = layout.getNonVirtualAlignment().getQuantity();
    }

    // C++-specific fields
    if (cxxRecord)
    {
        const CXXRecordDecl* primaryBase = layout.getPrimaryBase();
        bool hasOwnVFPtr = layout.hasOwnVFPtr();
        bool hasOwnVBPtr = layout.hasOwnVBPtr();

        // Add vtable pointer
        if (cxxRecord->isDynamicClass() && !primaryBase && !IsMsLayout(context))
        {
            // Itanium-style VTable pointer
            ret->AddField(PathogenRecordFieldKind::VTablePtr, 0, cxstring::createRef("vtable_pointer"), voidPointerPointerType);
        }
        else if (hasOwnVFPtr)
        {
            // Microsoft C++ ABI VFTable pointer
            ret->AddField(PathogenRecordFieldKind::VTablePtr, 0, cxstring::createRef("vftable_pointer"), voidPointerPointerType);
        }

        // Add non-virtual bases
        for (const CXXBaseSpecifier& base : cxxRecord->bases())
        {
            assert(!base.getType()->isDependentType() && "Cannot layout class with dependent bases.");

            // Ignore virtual bases, they come up later.
            if (base.isVirtual())
            { continue; }

            QualType baseType = base.getType();
            CXType cxType = cxtype::MakeCXType(baseType, translationUnit);
            CXXRecordDecl* baseRecord = baseType->getAsCXXRecordDecl();
            bool isPrimary = baseRecord == primaryBase;
            int64_t offset = layout.getBaseClassOffset(baseRecord).getQuantity();

            PathogenRecordField* field = ret->AddField(PathogenRecordFieldKind::NonVirtualBase, offset, cxstring::createRef(isPrimary ? "primary_base" : "base"), cxType);
            field->IsPrimaryBase = isPrimary;
        }

        // Vbptr - Microsoft C++ ABI
        if (hasOwnVBPtr)
        {
            ret->AddField(PathogenRecordFieldKind::VirtualBaseTablePtr, layout.getVBPtrOffset().getQuantity(), cxstring::createRef("vbtable_pointer"), voidPointerType);
        }
    }

    // Add normal fields
    uint64_t fieldIndex = 0;
    for (RecordDecl::field_iterator it = record->field_begin(), end = record->field_end(); it != end; it++, fieldIndex++)
    {
        const FieldDecl& field = **it;

        uint64_t offsetBits = layout.getFieldOffset(fieldIndex);
        CharUnits offsetChars = context.toCharUnitsFromBits(offsetBits);
        int64_t offset = offsetChars.getQuantity();

        PathogenRecordField* pathogenField = ret->AddField(PathogenRecordFieldKind::Normal, offset, translationUnit, field);

        // If the field is a bitfield, mark it as such.
        // This relies on the fields being offset-sequential since AddField doesn't know about bitfields.
        if (field.isBitField())
        {
            pathogenField->IsBitField = true;
            pathogenField->BitFieldStart = offsetBits - context.toBits(offsetChars);
            pathogenField->BitFieldWidth = field.getBitWidthValue(context);
        }
    }

    // Add virtual bases
    if (cxxRecord)
    {
        const ASTRecordLayout::VBaseOffsetsMapTy& vtorDisps = layout.getVBaseOffsetsMap();
        const CXXRecordDecl* primaryBase = layout.getPrimaryBase();

        for (const CXXBaseSpecifier& base : cxxRecord->vbases())
        {
            assert(base.isVirtual() && "Bases must be virtual.");
            QualType baseType = base.getType();
            CXType baseCxType = cxtype::MakeCXType(baseType, translationUnit);
            const CXXRecordDecl* vbase = baseType->getAsCXXRecordDecl();

            int64_t offset = layout.getVBaseClassOffset(vbase).getQuantity();

            if (vtorDisps.find(vbase)->second.hasVtorDisp())
            {
                ret->AddField(PathogenRecordFieldKind::VTorDisp, offset - 4, cxstring::createRef("vtordisp"), baseCxType);
            }

            bool isPrimary = vbase == primaryBase;
            PathogenRecordField* field = ret->AddField(PathogenRecordFieldKind::VirtualBase, offset, cxstring::createRef(isPrimary ? "primary_virtual_base" : "virtual_base"), baseCxType);
            field->IsPrimaryBase = isPrimary;
        }
    }

    // Add VTable layouts
    if (cxxRecord)
    {
        if (context.getVTableContext()->isMicrosoft())
        {
            MicrosoftVTableContext& vtableContext = *cast<MicrosoftVTableContext>(context.getVTableContext());
            const VPtrInfoVector& offsets = vtableContext.getVFPtrOffsets(cxxRecord);

            for (const std::unique_ptr<VPtrInfo>& offset : offsets)
            {
                const VTableLayout& layout = vtableContext.getVFTableLayout(cxxRecord, offset->FullOffsetInMDC);
                ret->AddVTableLayout(translationUnit, layout);
            }
        }
        else
        {
            ItaniumVTableContext& vtableContext = *cast<ItaniumVTableContext>(context.getVTableContext());
            const VTableLayout& layout = vtableContext.getVTableLayout(cxxRecord);
            ret->AddVTableLayout(translationUnit, layout);
        }
    }

    return ret;
}

PATHOGEN_EXPORT void pathogen_DeleteRecordLayout(PathogenRecordLayout* layout)
{
    delete layout;
}

struct PathogenTypeSizes
{
    int PathogenTypeSizes;
    int PathogenRecordLayout;
    int PathogenRecordField;
    int PathogenVTable;
    int PathogenVTableEntry;
};

//! Returns true if the sizes were populated, false if sizes->PathogenTypeSizes was invalid.
//! sizes->PathogenTypeSizes must be set to sizeof(PathogenTypeSizes)
PATHOGEN_EXPORT interop_bool pathogen_GetTypeSizes(PathogenTypeSizes* sizes)
{
    // Can't populate if the destination struct is the wrong size.
    if (sizes->PathogenTypeSizes != sizeof(PathogenTypeSizes))
    {
        return false;
    }

    sizes->PathogenRecordLayout = sizeof(PathogenRecordLayout);
    sizes->PathogenRecordField = sizeof(PathogenRecordField);
    sizes->PathogenVTable = sizeof(PathogenVTable);
    sizes->PathogenVTableEntry = sizeof(PathogenVTableEntry);
    return true;
}