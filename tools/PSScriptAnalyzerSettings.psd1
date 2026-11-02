@{
    # Correctness/security rules only; existing public command names remain stable.
    IncludeRules = @(
        'PSAvoidUsingInvokeExpression'
        'PSAvoidUsingPlainTextForPassword'
        'PSAvoidUsingConvertToSecureStringWithPlainText'
        'PSAvoidUsingUsernameAndPasswordParams'
        'PSAvoidUsingAllowUnencryptedAuthentication'
        'PSAvoidUsingBrokenHashAlgorithms'
        'PSAvoidUsingComputerNameHardcoded'
        'PSMisleadingBacktick'
        'PSReservedParams'
        'PSReservedCmdletChar'
        'PSAvoidInvokingEmptyMembers'
    )
}
