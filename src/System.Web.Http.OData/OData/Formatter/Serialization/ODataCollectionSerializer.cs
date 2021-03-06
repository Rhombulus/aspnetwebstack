﻿// Copyright (c) Microsoft Open Technologies, Inc. All rights reserved. See License.txt in the project root for license information.

using System.Collections;
using System.Diagnostics.Contracts;
using System.Web.Http.OData.Properties;
using Microsoft.Data.Edm;
using Microsoft.Data.OData;

namespace System.Web.Http.OData.Formatter.Serialization
{
    /// <summary>
    /// ODataSerializer for serializing collection of Entities or Complex types or primitives.
    /// </summary>
    internal class ODataCollectionSerializer : ODataEntrySerializer
    {
        private readonly IEdmCollectionTypeReference _edmCollectionType;
        private readonly IEdmTypeReference _edmItemType;

        public ODataCollectionSerializer(IEdmCollectionTypeReference edmCollectionType, ODataSerializerProvider serializerProvider)
            : base(edmCollectionType, ODataPayloadKind.Collection, serializerProvider)
        {
            Contract.Assert(edmCollectionType != null);
            _edmCollectionType = edmCollectionType;
            IEdmTypeReference itemType = edmCollectionType.ElementType();
            Contract.Assert(itemType != null);
            _edmItemType = itemType;
        }

        /// <inheritdoc/>
        public override void WriteObject(object graph, ODataMessageWriter messageWriter, ODataSerializerContext writeContext)
        {
            if (messageWriter == null)
            {
                throw Error.ArgumentNull("messageWriter");
            }

            if (writeContext == null)
            {
                throw Error.ArgumentNull("writeContext");
            }

            ODataCollectionWriter writer = messageWriter.CreateODataCollectionWriter(_edmItemType);
            writer.WriteStart(
                new ODataCollectionStart
                {
                    Name = writeContext.RootElementName
                });

            ODataValue value = CreateODataValue(graph, writeContext);
            if (value != null)
            {
                ODataCollectionValue collectionValue = value as ODataCollectionValue;
                Contract.Assert(value != null);

                foreach (object item in collectionValue.Items)
                {
                    writer.WriteItem(item);
                }
            }

            writer.WriteEnd();
            writer.Flush();
        }

        /// <inheritdoc/>
        public override ODataValue CreateODataValue(object graph, ODataSerializerContext writeContext)
        {
            if (writeContext == null)
            {
                throw Error.ArgumentNull("writeContext");
            }

            ArrayList valueCollection = new ArrayList();

            IEdmTypeReference itemType = _edmCollectionType.ElementType();
            ODataEntrySerializer itemSerializer = SerializerProvider.GetEdmTypeSerializer(itemType);
            if (itemSerializer == null)
            {
                throw Error.NotSupported(SRResources.TypeCannotBeSerialized, itemType.FullName(), typeof(ODataMediaTypeFormatter).Name);
            }

            IEnumerable enumerable = graph as IEnumerable;

            if (enumerable != null)
            {
                foreach (object item in enumerable)
                {
                    // ODataCollectionWriter expects the individual elements in the collection to be the underlying values
                    // and not ODataValues.
                    valueCollection.Add(itemSerializer.CreateODataValue(item, writeContext).GetInnerValue());
                }
            }

            // Ideally, we'd like to do this:
            // string typeName = _edmCollectionType.FullName();
            // But ODataLib currently doesn't support .FullName() for collections. As a workaround, we construct the
            // collection type name the hard way.
            string typeName = "Collection(" + _edmItemType.FullName() + ")";

            // ODataCollectionValue is only a V3 property, arrays inside Complex Types or Entity types are only supported in V3
            // if a V1 or V2 Client requests a type that has a collection within it ODataLib will throw.
            ODataCollectionValue value = new ODataCollectionValue
            {
                Items = valueCollection,
                TypeName = typeName
            };

            AddTypeNameAnnotationAsNeeded(value, writeContext.MetadataLevel);
            return value;
        }

        internal static void AddTypeNameAnnotationAsNeeded(ODataCollectionValue value,
            ODataMetadataLevel metadataLevel)
        {
            // ODataLib normally has the caller decide whether or not to serialize properties by leaving properties
            // null when values should not be serialized. The TypeName property is different and should always be
            // provided to ODataLib to enable model validation. A separate annotation is used to decide whether or not
            // to serialize the type name (a null value prevents serialization).

            // Note that this annotation should not be used for Atom or JSON verbose formats, as it will interfere with
            // the correct default behavior for those formats.

            Contract.Assert(value != null);

            // Only add an annotation if we want to override ODataLib's default type name serialization behavior.
            if (ShouldAddTypeNameAnnotation(metadataLevel))
            {
                string typeName;

                // Provide the type name to serialize (or null to force it not to serialize).
                if (ShouldSuppressTypeNameSerialization(metadataLevel))
                {
                    typeName = null;
                }
                else
                {
                    typeName = value.TypeName;
                }

                value.SetAnnotation<SerializationTypeNameAnnotation>(new SerializationTypeNameAnnotation
                {
                    TypeName = typeName
                });
            }
        }

        internal static bool ShouldAddTypeNameAnnotation(ODataMetadataLevel metadataLevel)
        {
            switch (metadataLevel)
            {
                // Don't interfere with the correct default behavior in non-JSON light formats.
                case ODataMetadataLevel.Default:
                // For collections, the default behavior matches the requirements for minimal metadata mode, so no
                // annotation is necessary.
                case ODataMetadataLevel.MinimalMetadata:
                    return false;
                // In other cases, this class must control the type name serialization behavior.
                case ODataMetadataLevel.FullMetadata:
                case ODataMetadataLevel.NoMetadata:
                default: // All values already specified; just keeping the compiler happy.
                    return true;
            }
        }

        internal static bool ShouldSuppressTypeNameSerialization(ODataMetadataLevel metadataLevel)
        {
            Contract.Assert(metadataLevel != ODataMetadataLevel.Default);
            Contract.Assert(metadataLevel != ODataMetadataLevel.MinimalMetadata);

            switch (metadataLevel)
            {
                case ODataMetadataLevel.NoMetadata:
                    return true;
                case ODataMetadataLevel.FullMetadata:
                default: // All values already specified; just keeping the compiler happy.
                    return false;
            }
        }
    }
}
