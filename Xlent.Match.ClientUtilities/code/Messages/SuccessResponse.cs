﻿using System.Runtime.Serialization;
using Xlent.Match.ClientUtilities.MatchObjectModel;

namespace Xlent.Match.ClientUtilities.Messages
{
    /// <summary>
    ///     Use this message when you successfully have handled a Match <see cref="Request" />.
    /// </summary>
    [DataContract(Name = "SuccessRequest", Namespace = "http://xlentmatch.com/")]
    public class SuccessResponse : Response
    {
        /// <summary>
        ///     The constructor for this class.
        /// </summary>
        /// <param name="request">The associated request.</param>
        /// <remarks><see cref="MatchObject" /> is the only property that is not set by this constructor.</remarks>
        public SuccessResponse(Request request)
            : base(request, ResponseTypeEnum.Success)
        {
        }
    }
}