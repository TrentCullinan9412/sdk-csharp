﻿// Copyright (c) Cloud Native Foundation.
// Licensed under the Apache 2.0 license.
// See LICENSE file in the project root for full license information.

using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Mime;
using System.Text;
using System.Threading.Tasks;

namespace CloudNative.CloudEvents.NewtonsoftJson
{
    // TODO: Rename to JsonCloudEventFormatter? NewtonsoftJsonCloudEventFormatter?

    /// <summary>
    /// Formatter that implements the JSON Event Format.
    /// </summary>
    /// <remarks>
    /// <para>
    /// When encoding CloudEvent data, the behavior of this implementation depends on the data
    /// content type of the CloudEvent and the type of the <see cref="CloudEvent.Data"/> property value,
    /// following the rules below. Derived classes can specialize this behavior by overriding
    /// <see cref="EncodeStructuredModeData(CloudEvent, JsonWriter)"/> or <see cref="EncodeBinaryModeEventData(CloudEvent)"/>.
    /// </para>
    /// <list type="bullet">
    /// <item><description>
    /// If the data value is null, the content is empty for a binary mode message, and neither the "data"
    /// nor "data_base64" property is populated in a structured mode message.
    /// </description></item>
    /// <item><description>
    /// If the data content type is absent or has a media type of "application/json", the data is encoded as JSON.
    /// If the data is already a <see cref="JToken"/>, that is serialized directly as JSON. Otherwise, the data
    /// is converted using the <see cref="JsonSerializer"/> passed into the constructor, or a
    /// default serializer.
    /// </description></item>
    /// <item><description>
    /// Otherwise, if the data content type has a media type beginning with "text/" and the data value is a string,
    /// the data is serialized as a string.
    /// </description></item>
    /// <item><description>
    /// Otherwise, if the data value is a byte array, it is serialized either directly as binary data
    /// (for binary mode messages) or as base64 data (for structured mode messages).
    /// </description></item>
    /// <item><description>
    /// Otherwise, the encoding operation fails.
    /// </description></item>
    /// </list>
    /// <para>
    /// When decoding CloudEvent data, this implementation uses the following rules:
    /// </para>
    /// <para>
    /// In a structured mode message, any data is either binary data within the "data_base64" property value,
    /// or is a JSON token as the "data" property value. Binary data is represented as a byte array.
    /// A JSON token is decoded as a string if is just a string value and the data content type is specified
    /// and has a media type beginning with "text/". A JSON token representing the null value always
    /// leads to a null data result. In any other situation, the JSON token is preserved as a <see cref="JToken"/>
    /// that can be used for further deserialization (e.g. to a specific CLR type). This behavior can be modified
    /// by overriding <see cref="DecodeStructuredModeDataBase64Property(JToken, CloudEvent)"/> and
    /// <see cref="DecodeStructuredModeDataProperty(JToken, CloudEvent)"/>.
    /// </para>
    /// <para>
    /// In a binary mode message, the data is parsed based on the content type of the message. When the content
    /// type is absent or has a media type of "application/json", the data is parsed as JSON, with the result as
    /// a <see cref="JToken"/> (or null if the data is empty). When the content type has a media type beginning
    /// with "text/", the data is parsed as a string. In all other cases, the data is left as a byte array.
    /// This behavior can be specialized by overriding <see cref="DecodeBinaryModeEventData(byte[], CloudEvent)"/>.
    /// </para>
    /// </remarks>
    public class JsonEventFormatter : CloudEventFormatter
    {
        private static readonly IReadOnlyDictionary<CloudEventAttributeType, JTokenType> expectedTokenTypesForReservedAttributes =
            new Dictionary<CloudEventAttributeType, JTokenType>
            {
                { CloudEventAttributeType.Binary, JTokenType.String },
                { CloudEventAttributeType.Boolean, JTokenType.Boolean },
                { CloudEventAttributeType.Integer, JTokenType.Integer },
                { CloudEventAttributeType.String, JTokenType.String },
                { CloudEventAttributeType.Timestamp, JTokenType.String },
                { CloudEventAttributeType.Uri, JTokenType.String },
                { CloudEventAttributeType.UriReference, JTokenType.String }
            };

        private const string JsonMediaType = "application/json";
        private const string MediaTypeSuffix = "+json";

        /// <summary>
        /// The property name to use for base64-encoded binary data in a structured-mode message.
        /// </summary>
        protected const string DataBase64PropertyName = "data_base64";

        /// <summary>
        /// The property name to use for general data in a structured-mode message.
        /// </summary>
        protected const string DataPropertyName = "data";

        /// <summary>
        /// The serializer to use when performing JSON conversions.
        /// </summary>
        protected JsonSerializer Serializer { get; }

        /// <summary>
        /// Creates a JsonEventFormatter that uses a default <see cref="JsonSerializer"/>.
        /// </summary>
        public JsonEventFormatter() : this(JsonSerializer.CreateDefault())
        {
        }

        /// <summary>
        /// Creates a JsonEventFormatter that uses the specified <see cref="JsonSerializer"/>
        /// to serialize objects as JSON.
        /// </summary>
        public JsonEventFormatter(JsonSerializer serializer)
        {
            Serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));
        }

        public override async Task<CloudEvent> DecodeStructuredModeMessageAsync(Stream data, ContentType contentType, IEnumerable<CloudEventAttribute> extensionAttributes)
        {
            var jsonReader = CreateJsonReader(data, contentType.GetEncoding());
            var jObject = await JObject.LoadAsync(jsonReader).ConfigureAwait(false);
            return DecodeJObject(jObject, extensionAttributes);
        }

        public override CloudEvent DecodeStructuredModeMessage(Stream data, ContentType contentType, IEnumerable<CloudEventAttribute> extensionAttributes)
        {
            var jsonReader = CreateJsonReader(data, contentType.GetEncoding());
            var jObject = JObject.Load(jsonReader);
            return DecodeJObject(jObject, extensionAttributes);
        }

        public override CloudEvent DecodeStructuredModeMessage(byte[] data, ContentType contentType, IEnumerable<CloudEventAttribute> extensionAttributes) =>
            DecodeStructuredModeMessage(new MemoryStream(data), contentType, extensionAttributes);

        private CloudEvent DecodeJObject(JObject jObject, IEnumerable<CloudEventAttribute> extensionAttributes = null)
        {
            if (!jObject.TryGetValue(CloudEventsSpecVersion.SpecVersionAttribute.Name, out var specVersionToken)
                || specVersionToken.Type != JTokenType.String)
            {
                throw new ArgumentException($"Structured mode content does not represent a CloudEvent");
            }
            var specVersion = CloudEventsSpecVersion.FromVersionId((string) specVersionToken);
            if (specVersion is null)
            {
                throw new ArgumentException($"Unsupported CloudEvents spec version '{(string)specVersionToken}'");
            }

            var cloudEvent = new CloudEvent(specVersion, extensionAttributes);
            PopulateAttributesFromStructuredEvent(cloudEvent, jObject);
            PopulateDataFromStructuredEvent(cloudEvent, jObject);
            return cloudEvent.Validate();
        }

        private void PopulateAttributesFromStructuredEvent(CloudEvent cloudEvent, JObject jObject)
        {
            foreach (var keyValuePair in jObject)
            {
                var key = keyValuePair.Key;
                var value = keyValuePair.Value;

                // Skip the spec version attribute, which we've already taken account of.
                // Data is handled later, when everything else (importantly, the data content type)
                // has been populated.
                if (key == CloudEventsSpecVersion.SpecVersionAttribute.Name ||
                    key == DataBase64PropertyName ||
                    key == DataPropertyName)
                {
                    continue;
                }

                // For non-extension attributes, validate that the token type is as expected.
                // We're more forgiving for extension attributes: if an integer-typed extension attribute
                // has a value of "10" (i.e. as a string), that's fine. (If it has a value of "garbage",
                // that will throw in SetAttributeFromString.)
                ValidateTokenTypeForAttribute(cloudEvent.GetAttribute(key), value.Type);

                // TODO: This currently performs more conversions than it really should, in the cause of simplicity.
                // We basically need a matrix of "attribute type vs token type" but that's rather complicated.

                string attributeValue = value.Type switch
                {
                    JTokenType.String => (string)value,
                    JTokenType.Boolean => CloudEventAttributeType.Boolean.Format((bool)value),
                    JTokenType.Null => null,
                    JTokenType.Integer => CloudEventAttributeType.Integer.Format((int)value),
                    _ => throw new ArgumentException($"Invalid token type '{value.Type}' for CloudEvent attribute")
                };
                if (attributeValue is null)
                {
                    continue;
                }
                // Note: we *could* infer an extension type of integer and Boolean, but not other extension types.
                // (We don't want to assume that everything that looks like a timestamp is a timestamp, etc.)
                // Stick to strings for consistency.
                cloudEvent.SetAttributeFromString(key, attributeValue);
            }
        }

        private void ValidateTokenTypeForAttribute(CloudEventAttribute attribute, JTokenType tokenType)
        {
            // We can't validate unknown attributes, don't check for extension attributes,
            // and null values will be ignored anyway.
            if (attribute is null || attribute.IsExtension || tokenType == JTokenType.Null)
            {
                return;
            }
            // We use TryGetValue so that if a new attribute type is added without this being updated, we "fail valid".
            // (That should only happen in major versions anyway, but it's worth being somewhat forgiving here.)
            if (expectedTokenTypesForReservedAttributes.TryGetValue(attribute.Type, out JTokenType expectedTokenType) &&
                tokenType != expectedTokenType)
            {
                throw new ArgumentException($"Invalid token type '{tokenType}' for CloudEvent attribute '{attribute.Name}' with type '{attribute.Type}'");
            }
        }

        private void PopulateDataFromStructuredEvent(CloudEvent cloudEvent, JObject jObject)
        {
            // Fetch data and data_base64 tokens, and treat null as missing.
            jObject.TryGetValue(DataPropertyName, out var dataToken);
            if (dataToken is JToken { Type: JTokenType.Null })
            {
                dataToken = null;
            }
            jObject.TryGetValue(DataBase64PropertyName, out var dataBase64Token);
            if (dataBase64Token is JToken { Type: JTokenType.Null })
            {
                dataBase64Token = null;
            }

            // If we don't have any data, we're done.
            if (dataToken is null && dataBase64Token is null)
            {
                return;
            }
            // We can't handle both properties being set.
            if (dataToken is object && dataBase64Token is object)
            {
                throw new ArgumentException($"Structured mode content cannot contain both '{DataPropertyName}' and '{DataBase64PropertyName}' properties.");
            }
            // Okay, we have exactly one non-null data/data_base64 property.
            // Decode it, potentially using overridden methods for specialization.
            if (dataBase64Token is object)
            {
                DecodeStructuredModeDataBase64Property(dataBase64Token, cloudEvent);
            }
            else
            {
                DecodeStructuredModeDataProperty(dataToken, cloudEvent);
            }
        }

        /// <summary>
        /// Decodes the "data_base64" property provided within a structured-mode message,
        /// populating the <see cref="CloudEvent.Data"/> property accordingly.
        /// </summary>
        /// <param name="cloudEvent"></param>
        /// <remarks>
        /// <para>
        /// This implementation converts JSON string tokens to byte arrays, and fails for any other token type.
        /// </para>
        /// <para>
        /// Override this method to provide more specialized conversions.
        /// </para>
        /// </remarks>
        /// <param name="dataBase64Token">The "data_base64" property value within the structured-mode message. Will not be null, and will
        /// not have a null token type.</param>
        /// <param name="cloudEvent">The event being decoded. This should not be modified except to
        /// populate the <see cref="CloudEvent.Data"/> property, but may be used to provide extra
        /// information such as the data content type. Will not be null.</param>
        /// <returns>The data to populate in the <see cref="CloudEvent.Data"/> property.</returns>
        protected virtual void DecodeStructuredModeDataBase64Property(JToken dataBase64Token, CloudEvent cloudEvent)
        {
            if (dataBase64Token.Type != JTokenType.String)
            {
                throw new ArgumentException($"Structured mode property '{DataBase64PropertyName}' must be a string, when present.");
            }
            cloudEvent.Data = Convert.FromBase64String((string)dataBase64Token);
        }

        /// <summary>
        /// Decodes the "data" property provided within a structured-mode message,
        /// populating the <see cref="CloudEvent.Data"/> property accordingly.
        /// </summary>
        /// <remarks>
        /// <para>
        /// This implementation converts JSON string tokens to strings when the content type suggests
        /// that's appropriate, but otherwise returns the token directly.
        /// </para>
        /// <para>
        /// Override this method to provide more specialized conversions.
        /// </para>
        /// </remarks>
        /// <param name="dataToken">The "data" property value within the structured-mode message. Will not be null, and will
        /// not have a null token type.</param>
        /// <param name="cloudEvent">The event being decoded. This should not be modified except to
        /// populate the <see cref="CloudEvent.Data"/> property, but may be used to provide extra
        /// information such as the data content type. Will not be null.</param>
        /// <returns>The data to populate in the <see cref="CloudEvent.Data"/> property.</returns>
        protected virtual void DecodeStructuredModeDataProperty(JToken dataToken, CloudEvent cloudEvent) =>
            cloudEvent.Data = dataToken.Type == JTokenType.String && cloudEvent.DataContentType?.StartsWith("text/") == true
                ? (string) dataToken
                : (object) dataToken; // Deliberately cast to object to avoid any implicit conversions

        public override byte[] EncodeStructuredModeMessage(CloudEvent cloudEvent, out ContentType contentType)
        {
            contentType = new ContentType("application/cloudevents+json")
            {
                CharSet = Encoding.UTF8.WebName
            };

            var stream = new MemoryStream();
            var writer = new JsonTextWriter(new StreamWriter(stream));
            writer.WriteStartObject();
            writer.WritePropertyName(CloudEventsSpecVersion.SpecVersionAttribute.Name);
            writer.WriteValue(cloudEvent.SpecVersion.VersionId);
            var attributes = cloudEvent.GetPopulatedAttributes();
            foreach (var keyValuePair in attributes)
            {
                var attribute = keyValuePair.Key;
                var value = keyValuePair.Value;
                writer.WritePropertyName(attribute.Name);
                // TODO: Maybe we should have an enum associated with CloudEventsAttributeType?
                if (attribute.Type == CloudEventAttributeType.Integer)
                {
                    writer.WriteValue((int)value);
                }
                else if (attribute.Type == CloudEventAttributeType.Boolean)
                {
                    writer.WriteValue((bool)value);
                }
                else
                {
                    writer.WriteValue(attribute.Type.Format(value));
                }
            }

            if (cloudEvent.Data is object)
            {
                EncodeStructuredModeData(cloudEvent, writer);
            }
            writer.WriteEndObject();
            writer.Flush();
            return stream.ToArray();
        }

        /// <summary>
        /// Encodes structured mode data within a CloudEvent, writing it to the specified <see cref="JsonWriter"/>.
        /// </summary>
        /// <remarks>
        /// <para>
        /// This implementation follows the rules listed in the class remarks. Override this method
        /// to provide more specialized behavior, writing only <see cref="DataPropertyName"/> or
        /// <see cref="DataBase64PropertyName"/> properties.
        /// </para>
        /// </remarks>
        /// <param name="cloudEvent">The CloudEvent being encoded, which will have a non-null value of
        /// <see cref="CloudEvent.Data"/>.</param>
        protected virtual void EncodeStructuredModeData(CloudEvent cloudEvent, JsonWriter writer)
        {
            ContentType dataContentType = new ContentType(cloudEvent.DataContentType ?? JsonMediaType);
            if (dataContentType.MediaType == JsonMediaType)
            {
                writer.WritePropertyName(DataPropertyName);
                Serializer.Serialize(writer, cloudEvent.Data);
            }
            else if (cloudEvent.Data is string text && dataContentType.MediaType.StartsWith("text/"))
            {
                writer.WritePropertyName(DataPropertyName);
                writer.WriteValue(text);
            }
            else if (cloudEvent.Data is byte[] binary)
            {
                writer.WritePropertyName(DataBase64PropertyName);
                writer.WriteValue(Convert.ToBase64String(binary));
            }
            else
            {
                throw new ArgumentException($"{nameof(JsonEventFormatter)} cannot serialize data of type {cloudEvent.Data.GetType()} with content type '{cloudEvent.DataContentType}'");
            }
        }

        public override byte[] EncodeBinaryModeEventData(CloudEvent cloudEvent)
        {
            if (cloudEvent.Data is null)
            {
                return Array.Empty<byte>();
            }
            ContentType contentType = new ContentType(cloudEvent.DataContentType ?? JsonMediaType);
            if (contentType.MediaType == JsonMediaType)
            {
                // TODO: Make this more efficient. We could write to a StreamWriter with a MemoryStream,
                // but then we end up with a BOM in most cases, which I suspect we don't want.
                // An alternative is to make sure that contentType.GetEncoding() always returns an encoding
                // without a preamble (or rewrite StreamWriter...)
                var stringWriter = new StringWriter();
                Serializer.Serialize(stringWriter, cloudEvent.Data);
                return contentType.GetEncoding().GetBytes(stringWriter.ToString());
            }
            if (contentType.MediaType.StartsWith("text/") && cloudEvent.Data is string text)
            {
                return contentType.GetEncoding().GetBytes(text);
            }
            if (cloudEvent.Data is byte[] bytes)
            {
                return bytes;
            }
            throw new ArgumentException($"{nameof(JsonEventFormatter)} cannot serialize data of type {cloudEvent.Data.GetType()} with content type '{cloudEvent.DataContentType}'");
        }

        public override void DecodeBinaryModeEventData(byte[] value, CloudEvent cloudEvent)
        {
            ContentType contentType = new ContentType(cloudEvent.DataContentType ?? JsonMediaType);

            Encoding encoding = contentType.GetEncoding();

            if (contentType.MediaType == JsonMediaType)
            {
                if (value.Length > 0)
                {
                    var jsonReader = CreateJsonReader(new MemoryStream(value), encoding);
                    cloudEvent.Data = JToken.Load(jsonReader);
                }
                else
                {
                    cloudEvent.Data = null;
                }
            }
            else if (contentType.MediaType.StartsWith("text/") == true)
            {
                cloudEvent.Data = encoding.GetString(value);
            }
            else
            {
                cloudEvent.Data = value;
            }
        }

        private static JsonReader CreateJsonReader(Stream stream, Encoding encoding) =>
            new JsonTextReader(new StreamReader(stream, encoding ?? Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 8192, leaveOpen: true))
            {
                DateParseHandling = DateParseHandling.None
            };
    }
}