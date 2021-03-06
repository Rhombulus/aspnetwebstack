﻿// Copyright (c) Microsoft Open Technologies, Inc. All rights reserved. See License.txt in the project root for license information.

using System.Collections.Specialized;

namespace System.Web.Helpers
{
    // Provides access to Request.* collections, except that these have not gone through request validation.
    public sealed class UnvalidatedRequestValues
    {
        private readonly HttpRequestBase _request;

        internal UnvalidatedRequestValues(HttpRequestBase request)
        {
            _request = request;
        }

        public NameValueCollection Form
        {
            get { return _request.Unvalidated.Form; }
        }

        public NameValueCollection QueryString
        {
            get { return _request.Unvalidated.QueryString; }
        }

        // this item getter follows the same logic as HttpRequest.get_Item
        public string this[string key]
        {
            get
            {
                string queryStringValue = QueryString[key];
                if (queryStringValue != null)
                {
                    return queryStringValue;
                }

                string formValue = Form[key];
                if (formValue != null)
                {
                    return formValue;
                }

                HttpCookie cookie = _request.Cookies[key];
                if (cookie != null)
                {
                    return cookie.Value;
                }

                string serverVarValue = _request.ServerVariables[key];
                if (serverVarValue != null)
                {
                    return serverVarValue;
                }

                return null;
            }
        }
    }
}