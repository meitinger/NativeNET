/* Copyright (C) 2011, Manuel Meitinger
 * 
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU General Public License as published by
 * the Free Software Foundation, either version 2 of the License, or
 * (at your option) any later version.
 * 
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
 * GNU General Public License for more details.
 * 
 * You should have received a copy of the GNU General Public License
 * along with this program.  If not, see <http://www.gnu.org/licenses/>.
 */

using System;
using System.Runtime.InteropServices;

namespace Aufbauwerk.Tools.NativeNET
{
    /// <summary>
    /// Specifies how a return value or parameter should be marshalled.
    /// NOTE: This attribute is necessary because <c>MarshalAsAttribute</c> cannot be reflected 'properly'.
    /// </summary>
    [AttributeUsage(AttributeTargets.ReturnValue | AttributeTargets.Parameter, AllowMultiple = false, Inherited = false)]
    public sealed class DllMarshalAsAttribute : Attribute
    {
        internal UnmanagedType unmanagedType;

        public UnmanagedType ArraySubType;
        public int IidParameterIndex;
        public VarEnum SafeArraySubType;
        public Type SafeArrayUserDefinedSubType;
        public int SizeConst;
        public short SizeParamIndex;

        public DllMarshalAsAttribute(UnmanagedType unmanagedType)
        {
            this.unmanagedType = unmanagedType;
        }

        public UnmanagedType Value { get { return this.unmanagedType; } }
    }
}
