﻿using System;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;

namespace Biohazrd.Transformation.Infrastructure
{
    /// <summary>Provides lazy <see cref="ImmutableList{TranslatedDeclaration}"/> building for <see cref="TransformationBase"/>.</summary>
    /// <remarks>
    /// This type is used to construct a <see cref="ImmutableList{TranslatedDeclaration}"/> when its contents might match an existing one.
    ///
    /// Use the <see cref="Add(TransformationResult)"/> method to add the result of a transformation.
    /// If the sequence of adds would've resulted in an identical collection, no new collection will be constructed and no additional memory will be allocated.
    ///
    /// Essentially this type is a lazyily-created <see cref="ImmutableList{TranslatedDeclaration}.Builder"/>.
    ///
    /// If you need to work with an <see cref="ImmutableList{T}"/> where <code>T</code> is more specific than <see cref="TranslatedDeclaration"/>, use <see cref="ListTransformHelper{TDeclaration}"/> instead.
    /// This type exists separately since it is a common case.
    /// </remarks>
    public ref struct ListTransformHelper
    {
        private readonly ImmutableList<TranslatedDeclaration> Original;
        private ImmutableList<TranslatedDeclaration>.Builder? Builder;
        private ImmutableList<TranslatedDeclaration>.Enumerator Enumerator;
        private int LastGoodIndex;
        private bool IsFinished;

        /// <summary>Indicates whether the additions have resulted in a modified collection yet.</summary>
        /// <remarks>Note that this will not indicate a truncated collection until <see cref="Finish"/> is called.</remarks>
        public bool WasChanged => Builder is not null;

        public ListTransformHelper(ImmutableList<TranslatedDeclaration> original)
        {
            Original = original;
            Builder = null;
            Enumerator = Original.GetEnumerator();
            LastGoodIndex = -1;
            IsFinished = false;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void ApplyToBuilder(TransformationResult transformation)
        {
            switch (transformation.Count)
            {
                case 1:
                    Builder!.Add(transformation.SingleDeclaration);
                    return;
                case > 1:
                    Builder!.AddRange(transformation.Declarations);
                    return;
            }
        }

        private void CreateBuilder(TransformationResult transformation)
        {
            Builder = ImmutableList.CreateBuilder<TranslatedDeclaration>();

            // Add all of the unchanged declarations earlier that we skipped
            int i = 0;
            foreach (TranslatedDeclaration declaration in Original)
            {
                if (i > LastGoodIndex)
                { break; }

                Builder.Add(declaration);
                i++;
            }

            // Apply the transformation
            ApplyToBuilder(transformation);

            // Once we have a builder, the enumerator is no longer needed so we dispose of it
            Enumerator.Dispose();
        }

        private bool CheckIsChange(TransformationResult transformation)
        {
            // Transformations with counts other than 1 always result in a change.
            if (transformation.Count != 1)
            { return true; }

            // Advance the enumerator to check what's in the original collection at the "current" location
            if (!Enumerator.MoveNext())
            {
                // The enumerator has no more elements, so this is an addition.
                return true;
            }

            // Check if the translations are the same
            TranslatedDeclaration oldDeclaration = Enumerator.Current;
            TranslatedDeclaration newDeclaration = transformation.SingleDeclaration;

            if (ReferenceEquals(oldDeclaration, newDeclaration))
            {
                // The references are equal, no change needed
                // Advanced LastGoodIndex to indicate that the elements up to this index are good
                LastGoodIndex++;
                return false;
            }
            else
            {
                // Equality check failed, the collection is being changed.
                return true;
            }
        }

        /// <summary>Appends the given transformation result to this collection.</summary>
        public void Add(TransformationResult transformation)
        {
            if (IsFinished)
            { throw new InvalidOperationException("Can't add to a collection once it's been finished."); }
            
            // If we were already changed, just add the results
            if (WasChanged)
            {
                ApplyToBuilder(transformation);
                return;
            }

            // If this transformation doesn't change the collection, do nothing.
            if (!CheckIsChange(transformation))
            { return; }

            // If we got this far, create a builder and apply our change
            CreateBuilder(transformation);
        }

        /// <summary>Indicates that the collection is complete and no more calls to <see cref="Add(TransformationResult)"/> will be performed.</summary>
        /// <remarks>
        /// Calling this method will change the value of <see cref="WasChanged"/> in the event the collection was truncated.
        ///
        /// After marking a collection as finished, you can no longer call <see cref="Add(TransformationResult)"/>.
        /// </remarks>
        public void Finish()
        {
            // If we've already been finished, there's nothing to do.
            if (IsFinished)
            { return; }

            // Mark this instance as finished to prevent further adds
            IsFinished = true;

            // If we were changed there is nothing to do
            if (WasChanged)
            { return; }

            // Advance the enumerator, if it ran out of elements we exhausted the entire original list
            if (!Enumerator.MoveNext())
            {
                // The enumerator is no longer needed at this point, so we dispose of it
                Enumerator.Dispose();
                return;
            }

            // If we *didn't* exhaust the entire original collection, the collection was truncated so we'll need to create a new one.
            // (A default TransformationResult normally results in a deletion, but in this case we use it to add nothing.)
            CreateBuilder(default);
        }

        /// <summary>Constructs a new <see cref="ImmutableList{TranslatedDeclaration}"/> based on the changes made to this instance.</summary>
        /// <returns>A modified collection if changes were made, the original collection otherwise.</returns>
        /// <remarks>This method automatically calls <see cref="Finish"/>. As such, you can no longer add to this instance after calling this method.</remarks>
        public ImmutableList<TranslatedDeclaration> ToImmutable()
        {
            Finish();
            return Builder?.ToImmutable() ?? Original;
        }

        /// <summary>Disposes of this instance, releasing pooled resources.</summary>
        /// <remarks>
        /// This method disposes of pool resources held by the internal <see cref="ImmutableList{TranslatedDeclaration}.Enumerator"/>.
        /// 
        /// Generally speaking, disposing of this instance is not strictly necessary because it is done automatically when <see cref="Finish"/> or <see cref="ToImmutable"/> is called.
        /// </remarks>
        public void Dispose()
            => Enumerator.Dispose();
    }
}
