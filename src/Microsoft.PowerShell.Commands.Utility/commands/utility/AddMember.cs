// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Management.Automation;
using System.Management.Automation.Host;
using System.Management.Automation.Internal;
using System.Reflection;

namespace Microsoft.PowerShell.Commands
{
    /// <summary>
    /// This class implements get-member command.
    /// </summary>
    [Cmdlet(VerbsCommon.Add, "Member", DefaultParameterSetName = TypeNameSet,
        HelpUri = "https://go.microsoft.com/fwlink/?LinkID=113280", RemotingCapability = RemotingCapability.None)]
    public class AddMemberCommand : PSCmdlet
    {
        private static readonly object _valueNotSpecified = new object();
        private static bool HasBeenSpecified(object obj) => !System.Object.ReferenceEquals(obj, _valueNotSpecified);

        /// <summary>
        /// The object to add a member to.
        /// </summary>
        [Parameter(Mandatory = true, ValueFromPipeline = true, ParameterSetName = "MemberSet")]
        [Parameter(Mandatory = true, ValueFromPipeline = true, ParameterSetName = TypeNameSet)]
        [Parameter(Mandatory = true, ValueFromPipeline = true, ParameterSetName = NotePropertySingleMemberSet)]
        [Parameter(Mandatory = true, ValueFromPipeline = true, ParameterSetName = NotePropertyMultiMemberSet)]
        public PSObject InputObject { get; set; }

        /// <summary>
        /// The member type of to be added.
        /// </summary>
        [Parameter(Mandatory = true, Position = 0, ParameterSetName = "MemberSet")]
        [Alias("Type")]
        public PSMemberTypes MemberType { get; set; }

        /// <summary>
        /// The name of the new member.
        /// </summary>
        [Parameter(Mandatory = true, Position = 1, ParameterSetName = "MemberSet")]
        public string Name { get; set; }

        /// <summary>
        /// First value of the new member. The meaning of this value changes according to the member type.
        /// </summary>
        [Parameter(Position = 2, ParameterSetName = "MemberSet")]
        public object Value { get; set; } = _valueNotSpecified;

        /// <summary>
        /// Second value of the new member. The meaning of this value changes according to the member type.
        /// </summary>
        [Parameter(Position = 3, ParameterSetName = "MemberSet")]
        public object SecondValue { get; set; } = _valueNotSpecified;

        /// <summary>
        /// Add new type name to the specified object for TypeNameSet.
        /// </summary>
        [Parameter(Mandatory = true, ParameterSetName = TypeNameSet)]
        [Parameter(ParameterSetName = "MemberSet")]
        [Parameter(ParameterSetName = NotePropertySingleMemberSet)]
        [Parameter(ParameterSetName = NotePropertyMultiMemberSet)]
        [ValidateNotNullOrEmpty]
        public string TypeName { get; set; }

        /// <summary>
        /// True if we should overwrite a possibly existing member.
        /// </summary>
        [Parameter(ParameterSetName = "MemberSet")]
        [Parameter(ParameterSetName = NotePropertySingleMemberSet)]
        [Parameter(ParameterSetName = NotePropertyMultiMemberSet)]
        public SwitchParameter Force { get; set; }

        /// <summary>
        /// Gets or sets the parameter -passThru which states output from the command should be placed in the pipeline.
        /// </summary>
        [Parameter(ParameterSetName = "MemberSet")]
        [Parameter(ParameterSetName = TypeNameSet)]
        [Parameter(ParameterSetName = NotePropertySingleMemberSet)]
        [Parameter(ParameterSetName = NotePropertyMultiMemberSet)]
        public SwitchParameter PassThru { get; set; }

        #region Simplifying NoteProperty Declaration

        private const string TypeNameSet = "TypeNameSet";
        private const string NotePropertySingleMemberSet = "NotePropertySingleMemberSet";
        private const string NotePropertyMultiMemberSet = "NotePropertyMultiMemberSet";

        /// <summary>
        /// The name of the new NoteProperty member.
        /// </summary>
        [Parameter(Mandatory = true, Position = 0, ParameterSetName = NotePropertySingleMemberSet)]
        [ValidateNotePropertyNameAttribute()]
        [NotePropertyTransformationAttribute()]
        [ValidateNotNullOrEmpty]
        public string NotePropertyName { get; set; }

        /// <summary>
        /// The value of the new NoteProperty member.
        /// </summary>
        [Parameter(Mandatory = true, Position = 1, ParameterSetName = NotePropertySingleMemberSet)]
        [AllowNull]
        public object NotePropertyValue { get; set; }

        /// <summary>
        /// The NoteProperty members to be set. Uses IDictionary to support both Hashtable and OrderedHashtable.
        /// </summary>
        [Parameter(Mandatory = true, Position = 0, ParameterSetName = NotePropertyMultiMemberSet)]
        [ValidateNotNullOrEmpty]
        [SuppressMessage("Microsoft.Usage", "CA2227:CollectionPropertiesShouldBeReadOnly")]
        public IDictionary NotePropertyMembers { get; set; }

        #endregion Simplifying NoteProperty Declaration

        private static object GetParameterType(object sourceValue, Type destinationType)
            => LanguagePrimitives.ConvertTo(sourceValue, destinationType, CultureInfo.InvariantCulture);

        private void EnsureValue1AndValue2AreNotBothNull()
        {
            if (Value == null
                && (SecondValue == null || !HasBeenSpecified(SecondValue)))
            {
                ThrowTerminatingError(NewError("Value1AndValue2AreNotBothNull", "Value1AndValue2AreNotBothNull", null, MemberType));
            }
        }

        private void EnsureValue1IsNotNull()
        {
            if (Value == null)
            {
                ThrowTerminatingError(NewError("Value1ShouldNotBeNull", "Value1ShouldNotBeNull", null, MemberType));
            }
        }

        private void EnsureValue2IsNotNull()
        {
            if (SecondValue == null)
            {
                ThrowTerminatingError(NewError("Value2ShouldNotBeNull", "Value2ShouldNotBeNull", null, MemberType));
            }
        }

        private void EnsureValue1HasBeenSpecified()
        {
            if (!HasBeenSpecified(Value))
            {
                var fieldDescriptors = new Collection<FieldDescription>
                {
                    new FieldDescription("Value")
                };
                var prompt = StringUtil.Format(AddMember.Value1Prompt, MemberType);
                Dictionary<string, PSObject> result = Host.UI.Prompt(prompt, null, fieldDescriptors);
                if (result != null)
                {
                    Value = result["Value"].BaseObject;
                }
            }
        }

        private void EnsureValue2HasNotBeenSpecified()
        {
            if (HasBeenSpecified(SecondValue))
            {
                ThrowTerminatingError(NewError("Value2ShouldNotBeSpecified", "Value2ShouldNotBeSpecified", null, MemberType));
            }
        }

        private PSMemberInfo GetAliasProperty()
        {
            EnsureValue1HasBeenSpecified();
            EnsureValue1IsNotNull();

            var value1Str = (string)GetParameterType(Value, typeof(string));
            if (HasBeenSpecified(SecondValue))
            {
                EnsureValue2IsNotNull();
                var value2Type = (Type)GetParameterType(SecondValue, typeof(Type));
                return new PSAliasProperty(Name, value1Str, value2Type);
            }

            return new PSAliasProperty(Name, value1Str);
        }

        private PSMemberInfo GetCodeMethod()
        {
            EnsureValue1HasBeenSpecified();
            EnsureValue1IsNotNull();
            EnsureValue2HasNotBeenSpecified();

            MethodInfo value1MethodInfo = (MethodInfo)GetParameterType(Value, typeof(MethodInfo));
            return new PSCodeMethod(Name, value1MethodInfo);
        }

        private PSMemberInfo GetCodeProperty()
        {
            EnsureValue1HasBeenSpecified();
            EnsureValue1AndValue2AreNotBothNull();

            MethodInfo value1MethodInfo = HasBeenSpecified(Value)
                ? (MethodInfo)GetParameterType(Value, typeof(MethodInfo))
                : null;

            MethodInfo value2MethodInfo = HasBeenSpecified(SecondValue)
                ? (MethodInfo)GetParameterType(SecondValue, typeof(MethodInfo))
                : null;

            return new PSCodeProperty(Name, value1MethodInfo, value2MethodInfo);
        }

        private PSMemberInfo GetMemberSet()
        {
            EnsureValue2HasNotBeenSpecified();
            if (Value == null || !HasBeenSpecified(Value))
            {
                return new PSMemberSet(Name);
            }

            var value1Collection = (Collection<PSMemberInfo>)GetParameterType(Value, typeof(Collection<PSMemberInfo>));
            return new PSMemberSet(Name, value1Collection);
        }

        private PSMemberInfo GetNoteProperty()
        {
            EnsureValue1HasBeenSpecified();
            EnsureValue2HasNotBeenSpecified();

            return new PSNoteProperty(Name, Value);
        }

        private PSMemberInfo GetPropertySet()
        {
            EnsureValue2HasNotBeenSpecified();
            EnsureValue1HasBeenSpecified();
            EnsureValue1IsNotNull();

            var value1Collection = (Collection<string>)GetParameterType(Value, typeof(Collection<string>));
            return new PSPropertySet(Name, value1Collection);
        }

        private PSMemberInfo GetScriptMethod()
        {
            EnsureValue2HasNotBeenSpecified();
            EnsureValue1HasBeenSpecified();
            EnsureValue1IsNotNull();

            var value1ScriptBlock = (ScriptBlock)GetParameterType(Value, typeof(ScriptBlock));
            return new PSScriptMethod(Name, value1ScriptBlock);
        }

        private PSMemberInfo GetScriptProperty()
        {
            EnsureValue1HasBeenSpecified();
            EnsureValue1AndValue2AreNotBothNull();

            ScriptBlock value1ScriptBlock = HasBeenSpecified(Value)
                ? (ScriptBlock)GetParameterType(Value, typeof(ScriptBlock))
                : null;

            ScriptBlock value2ScriptBlock = HasBeenSpecified(SecondValue)
                ? (ScriptBlock)GetParameterType(SecondValue, typeof(ScriptBlock))
                : null;

            return new PSScriptProperty(Name, value1ScriptBlock, value2ScriptBlock);
        }

        /// <summary>
        /// This method implements the ProcessRecord method for add-member command.
        /// </summary>
        protected override void ProcessRecord()
        {
            if (TypeName != null && string.IsNullOrWhiteSpace(TypeName))
            {
                ThrowTerminatingError(NewError("TypeNameShouldNotBeEmpty", "TypeNameShouldNotBeEmpty", TypeName));
            }

            PSMemberInfo member;
            switch (ParameterSetName)
            {
                case TypeNameSet:
                    UpdateTypeNames();

                    if (PassThru)
                    {
                        WriteObject(InputObject);
                    }

                    return;
                case NotePropertyMultiMemberSet:
                    ProcessNotePropertyMultiMemberSet();
                    return;
                case NotePropertySingleMemberSet:
                    member = new PSNoteProperty(NotePropertyName, NotePropertyValue);
                    break;
                default:
                    int memberCountHelper = (int)MemberType;
                    int memberCount = 0;
                    while (memberCountHelper != 0)
                    {
                        if ((memberCountHelper & 1) != 0)
                        {
                            memberCount++;
                        }

                        memberCountHelper >>= 1;
                    }

                    if (memberCount != 1)
                    {
                        ThrowTerminatingError(
                            NewError("WrongMemberCount", "WrongMemberCount", null, MemberType.ToString()));
                        return;
                    }

                    member = GetMember(MemberType);
                    break;
            }

            if (member == null || !TryAddMemberToTarget(member))
            {
                return;
            }

            if (TypeName != null)
            {
                UpdateTypeNames();
            }

            if (PassThru)
            {
                WriteObject(InputObject);
            }
        }

        private PSMemberInfo GetMember(PSMemberTypes memberType)
        {
            PSMemberInfo result = memberType switch
            {
                PSMemberTypes.AliasProperty => GetAliasProperty(),
                PSMemberTypes.CodeMethod => GetCodeMethod(),
                PSMemberTypes.CodeProperty => GetCodeProperty(),
                PSMemberTypes.MemberSet => GetMemberSet(),
                PSMemberTypes.NoteProperty => GetNoteProperty(),
                PSMemberTypes.PropertySet => GetPropertySet(),
                PSMemberTypes.ScriptMethod => GetScriptMethod(),
                PSMemberTypes.ScriptProperty => GetScriptProperty(),
                _ => null
            };

            if (result == null)
            {
                ThrowTerminatingError(
                    NewError("CannotAddMemberType", "CannotAddMemberType", null, MemberType.ToString()));
            }

            return result;
        }

        /// <summary>
        /// Add the member to the target object.
        /// </summary>
        /// <param name="member"></param>
        /// <returns></returns>
        private bool TryAddMemberToTarget(PSMemberInfo member)
        {
            PSMemberInfo previousMember = InputObject.Members[member.Name];
            if (previousMember != null)
            {
                if (!Force)
                {
                    WriteError(NewError(
                        "MemberAlreadyExists",
                        "MemberAlreadyExists",
                        InputObject,
                        member.Name));
                    return false;
                }
                else
                {
                    if (previousMember.IsInstance)
                    {
                        InputObject.Members.Remove(member.Name);
                    }
                    else
                    {
                        WriteError(NewError(
                            "CannotRemoveTypeDataMember",
                            "CannotRemoveTypeDataMember",
                            InputObject,
                            member.Name,
                            previousMember.MemberType));
                        return false;
                    }
                }
            }

            InputObject.Members.Add(member);
            return true;
        }

        /// <summary>
        /// Process the 'NotePropertyMultiMemberSet' parameter set.
        /// </summary>
        private void ProcessNotePropertyMultiMemberSet()
        {
            bool result = false;
            foreach (DictionaryEntry prop in NotePropertyMembers)
            {
                string noteName = PSObject.ToStringParser(this.Context, prop.Key);
                object noteValue = prop.Value;

                if (string.IsNullOrEmpty(noteName))
                {
                    WriteError(NewError(
                        "NotePropertyNameShouldNotBeNull",
                        "NotePropertyNameShouldNotBeNull",
                        noteName));
                    continue;
                }

                PSMemberInfo member = new PSNoteProperty(noteName, noteValue);
                if (TryAddMemberToTarget(member) && !result)
                {
                    result = true;
                }
            }

            if (result && TypeName != null)
            {
                UpdateTypeNames();
            }

            if (result && PassThru)
            {
                WriteObject(InputObject);
            }
        }

        private void UpdateTypeNames()
        {
            // Respect the type shortcut
            string typeNameInUse = TypeName;
            if (LanguagePrimitives.TryConvertTo(TypeName, out Type type))
            {
                typeNameInUse = type.FullName;
            }

            InputObject.TypeNames.Insert(0, typeNameInUse);
        }

        private ErrorRecord NewError(string errorId, string resourceId, object targetObject, params object[] args)
        {
            ErrorDetails details = new ErrorDetails(
                this.GetType().GetTypeInfo().Assembly,
                "Microsoft.PowerShell.Commands.Utility.resources.AddMember",
                resourceId,
                args);
            ErrorRecord errorRecord = new ErrorRecord(
                new InvalidOperationException(details.Message),
                errorId,
                ErrorCategory.InvalidOperation,
                targetObject);
            return errorRecord;
        }

        /// <summary>
        /// This ValidateArgumentsAttribute is used to guarantee the argument to be bound to
        /// -NotePropertyName parameter cannot be converted to the enum type PSMemberTypes.
        /// So when given a string or a number that can be converted, we make sure it gets
        /// bound to -MemberType, instead of -NotePropertyName.
        /// </summary>
        /// <remarks>
        /// This exception will be hidden in the positional binding phase. So we make sure
        /// if the argument can be converted to PSMemberTypes, it gets bound to the -MemberType
        /// parameter. We are sure that when this exception is thrown, the current positional
        /// argument can be successfully bound to.
        /// </remarks>
        private sealed class ValidateNotePropertyNameAttribute : ValidateArgumentsAttribute
        {
            protected override void Validate(object arguments, EngineIntrinsics engineIntrinsics)
            {
                if (arguments is string notePropertyName
                    && LanguagePrimitives.TryConvertTo<PSMemberTypes>(notePropertyName, out PSMemberTypes memberType))
                {
                    switch (memberType)
                    {
                        case PSMemberTypes.AliasProperty:
                        case PSMemberTypes.CodeMethod:
                        case PSMemberTypes.CodeProperty:
                        case PSMemberTypes.MemberSet:
                        case PSMemberTypes.NoteProperty:
                        case PSMemberTypes.PropertySet:
                        case PSMemberTypes.ScriptMethod:
                        case PSMemberTypes.ScriptProperty:
                            throw new ValidationMetadataException(
                                message: StringUtil.Format(
                                    AddMember.InvalidValueForNotePropertyName, typeof(PSMemberTypes).FullName),
                                swallowException: true);
                        default:
                            break;
                    }
                }
            }
        }

        /// <summary>
        /// Transform the integer arguments to strings for the parameter NotePropertyName.
        /// </summary>
        internal sealed class NotePropertyTransformationAttribute : ArgumentTransformationAttribute
        {
            public override object Transform(EngineIntrinsics engineIntrinsics, object inputData)
            {
                object target = PSObject.Base(inputData);
                if (target != null && target.GetType().IsNumeric())
                {
                    return LanguagePrimitives.ConvertTo<string>(target);
                }

                return inputData;
            }
        }
    }
}
